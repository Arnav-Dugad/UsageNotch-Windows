using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using UsageNotch.Models;

namespace UsageNotch.Services;

public sealed record UsagePoint(DateTimeOffset At, double Used, DateTimeOffset? Reset, string Period);
public sealed record UsageEvent(DateTimeOffset At, string Kind, string Explanation);

/// <summary>Local observations with no automatic expiry. Never stores credentials or provider error text.</summary>
public sealed class UsageHistory
{
    private readonly string _path;
    private readonly Dictionary<string, string> _anonymousSessions = new();
    public string? Error { get; private set; }
    public UsageHistory(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageNotch", "history.db");

    public string AccountKey(ProviderSnapshot snapshot)
    {
        // Providers without an identity start a separate series each process, avoiding cross-login forecasts.
        if (string.IsNullOrWhiteSpace(snapshot.AccountName))
        {
            if (!_anonymousSessions.TryGetValue(snapshot.Id, out var key))
                _anonymousSessions[snapshot.Id] = key = Guid.NewGuid().ToString("N");
            return key;
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.Id + "\n" + snapshot.AccountName)));
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS observations (
                provider TEXT NOT NULL, account TEXT NOT NULL, window TEXT NOT NULL,
                at INTEGER NOT NULL, used REAL NOT NULL, reset INTEGER, period TEXT NOT NULL,
                PRIMARY KEY(provider, account, window, at));
            CREATE TABLE IF NOT EXISTS history_events (
                provider TEXT NOT NULL, account TEXT NOT NULL, window TEXT NOT NULL,
                at INTEGER NOT NULL, kind TEXT NOT NULL, explanation TEXT NOT NULL,
                PRIMARY KEY(provider, account, window, at, kind));
            CREATE TABLE IF NOT EXISTS provider_state (provider TEXT PRIMARY KEY, account TEXT NOT NULL, status TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS day_notes (date TEXT PRIMARY KEY, text TEXT NOT NULL, at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS plan_history (provider TEXT NOT NULL, account TEXT NOT NULL, at INTEGER NOT NULL, plan TEXT NOT NULL,
                PRIMARY KEY(provider, account, at));
            """;
        command.ExecuteNonQuery();
        return connection;
    }

    public void Record(ProviderSnapshot snapshot, DateTimeOffset now)
    {
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var account = AccountKey(snapshot);
            void Event(string window, DateTimeOffset when, string kind, string explanation)
            {
                using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
                cmd.CommandText = "INSERT OR IGNORE INTO history_events VALUES ($p,$a,$w,$t,$k,$e)";
                cmd.Parameters.AddWithValue("$p", snapshot.Id); cmd.Parameters.AddWithValue("$a", account);
                cmd.Parameters.AddWithValue("$w", window); cmd.Parameters.AddWithValue("$t", when.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$k", kind); cmd.Parameters.AddWithValue("$e", explanation); cmd.ExecuteNonQuery();
            }
            using (var state = connection.CreateCommand())
            {
                state.Transaction = transaction; state.CommandText = "SELECT account,status FROM provider_state WHERE provider=$p";
                state.Parameters.AddWithValue("$p", snapshot.Id);
                using var reader = state.ExecuteReader();
                if (reader.Read())
                {
                    if (reader.GetString(0) != account) Event("", now, "Account boundary", "Account identity changed, or an unidentified provider began a new app session. Histories remain separate.");
                    if (reader.GetString(1) != snapshot.Status.ToString())
                        Event("", now, snapshot.Status == SnapshotStatus.Ok ? "Readings resumed" : "Reading unavailable",
                            snapshot.Status == SnapshotStatus.Ok ? "Successful provider readings resumed." : "The app could not obtain a current successful reading. This does not establish a provider-wide outage.");
                }
            }
            // Plan history: a row whenever the plan the provider reports differs from the last one seen.
            if (snapshot.Status == SnapshotStatus.Ok && snapshot.Plan is { Length: > 0 } plan)
            {
                using var last = connection.CreateCommand(); last.Transaction = transaction;
                last.CommandText = "SELECT plan FROM plan_history WHERE provider=$p AND account=$a ORDER BY at DESC LIMIT 1";
                last.Parameters.AddWithValue("$p", snapshot.Id); last.Parameters.AddWithValue("$a", account);
                if (last.ExecuteScalar() is not string previousPlan || previousPlan != plan)
                {
                    using var add = connection.CreateCommand(); add.Transaction = transaction;
                    add.CommandText = "INSERT OR IGNORE INTO plan_history VALUES ($p,$a,$t,$n)";
                    add.Parameters.AddWithValue("$p", snapshot.Id); add.Parameters.AddWithValue("$a", account);
                    add.Parameters.AddWithValue("$t", now.ToUnixTimeMilliseconds()); add.Parameters.AddWithValue("$n", plan.Length > 40 ? plan[..40] : plan);
                    add.ExecuteNonQuery();
                }
            }
            using (var state = connection.CreateCommand())
            {
                state.Transaction = transaction; state.CommandText = "INSERT OR REPLACE INTO provider_state VALUES ($p,$a,$s)";
                state.Parameters.AddWithValue("$p", snapshot.Id); state.Parameters.AddWithValue("$a", account);
                state.Parameters.AddWithValue("$s", snapshot.Status.ToString()); state.ExecuteNonQuery();
            }
            if (snapshot.Status != SnapshotStatus.Ok || snapshot.FetchedAt is not { } at || at > now.AddMinutes(1) || now - at > TimeSpan.FromMinutes(15))
            { transaction.Commit(); return; }
            foreach (var window in snapshot.Windows)
            {
                if (window.UsedFraction is not { } used || !double.IsFinite(used) || used < 0) continue;
                using var previous = connection.CreateCommand();
                previous.Transaction = transaction;
                previous.CommandText = "SELECT at, used, reset, period FROM observations WHERE provider=$p AND account=$a AND window=$w ORDER BY at DESC LIMIT 1";
                previous.Parameters.AddWithValue("$p", snapshot.Id);
                previous.Parameters.AddWithValue("$a", account);
                previous.Parameters.AddWithValue("$w", window.Id);
                string period = Guid.NewGuid().ToString("N");
                using (var reader = previous.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        var lastAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0));
                        var reset = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
                        if (at <= lastAt) continue;
                        var newReset = window.ResetsAt?.ToUnixTimeMilliseconds();
                        var sameReset = reset is null ? newReset is null : newReset is { } reported && Math.Abs(reported - reset.Value) <= 120_000;
                        if (at - lastAt > TimeSpan.FromMinutes(20)) Event(window.Id, at, "Sampling gap", $"No observations for {(at - lastAt).TotalMinutes:0} minutes. Consumption during this interval is unknown.");
                        if (!sameReset || used < reader.GetDouble(1) - .0001)
                        {
                            var confirmed = reset is { } old && at.ToUnixTimeMilliseconds() >= old && newReset > old && used < reader.GetDouble(1) - .0001;
                            Event(window.Id, at, confirmed ? "Confirmed reset" : "Window changed", confirmed
                                ? "A lower usage reading and an advanced provider reset time confirm a reset between observations. The marker is the confirming reading, not an invented exact reset instant."
                                : "The provider changed its reset time or reported lower usage. A new observation period begins; a reset is not assumed.");
                        }
                        if (at - lastAt <= TimeSpan.FromMinutes(20) && used >= reader.GetDouble(1) - .0001 && sameReset
                            && (reset is null || lastAt.ToUnixTimeMilliseconds() < reset)) period = reader.GetString(3);
                    }
                }
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT OR IGNORE INTO observations VALUES ($p,$a,$w,$t,$u,$r,$c)";
                insert.Parameters.AddWithValue("$p", snapshot.Id);
                insert.Parameters.AddWithValue("$a", account);
                insert.Parameters.AddWithValue("$w", window.Id);
                insert.Parameters.AddWithValue("$t", at.ToUnixTimeMilliseconds());
                insert.Parameters.AddWithValue("$u", used);
                insert.Parameters.AddWithValue("$r", (object?)window.ResetsAt?.ToUnixTimeMilliseconds() ?? DBNull.Value);
                insert.Parameters.AddWithValue("$c", period);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
            Error = null;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { Error = "History is temporarily unavailable. Live usage still works."; }
    }

    public IReadOnlyList<UsagePoint> Read(string provider, string account, string window, DateTimeOffset since, DateTimeOffset? until = null)
    {
        var points = new List<UsagePoint>();
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT at,used,reset,period FROM observations WHERE provider=$p AND account=$a AND window=$w AND at >= $s AND at <= $end ORDER BY at";
            command.Parameters.AddWithValue("$end", (until ?? DateTimeOffset.MaxValue).ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$p", provider);
            command.Parameters.AddWithValue("$a", account);
            command.Parameters.AddWithValue("$w", window);
            command.Parameters.AddWithValue("$s", since.ToUnixTimeMilliseconds());
            using var reader = command.ExecuteReader();
            while (reader.Read()) points.Add(new(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)), reader.GetDouble(1),
                reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)), reader.GetString(3)));
            Error = null;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { Error = "History is temporarily unavailable. Live usage still works."; }
        return points;
    }

    /// <summary>The plans this account has reported, oldest first: when UsageNotch first saw each one.</summary>
    public IReadOnlyList<PlanChange> Plans(string provider, string account, int max = 20)
    {
        var list = new List<PlanChange>();
        try
        {
            using var connection = Open(); using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT at, plan FROM plan_history WHERE provider=$p AND account=$a ORDER BY at";
            cmd.Parameters.AddWithValue("$p", provider); cmd.Parameters.AddWithValue("$a", account);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new PlanChange(DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)), r.GetString(1)));
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { }
        return list.TakeLast(max).ToArray();
    }

    /// <summary>Every day note, newest first, including deleted ones (empty text) so phones learn about deletions.</summary>
    public IReadOnlyList<DayNote> Notes(int max = 500)
    {
        var list = new List<DayNote>();
        try
        {
            using var connection = Open(); using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT date, text, at FROM day_notes ORDER BY date DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", max);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (DateTime.TryParseExact(r.GetString(0), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
                    list.Add(new DayNote(date, r.GetString(1), r.GetInt64(2)));
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>
    /// Merges notes by time: a note replaces ours only when it's newer (a tie keeps ours). Empty text deletes. Dates must be
    /// real days from 2020 to tomorrow, and times no later than a day from now. Returns true when anything changed.
    /// </summary>
    public bool MergeNotes(IEnumerable<DayNote> incoming, DateTimeOffset now)
    {
        var changed = false;
        try
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            var ceiling = now.AddDays(1).ToUnixTimeMilliseconds(); var latest = now.ToLocalTime().Date.AddDays(1);
            foreach (var note in incoming)
            {
                if (note.At <= 0 || note.At > ceiling || note.Date.Year < 2020 || note.Date > latest) continue;
                var key = note.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                using var read = connection.CreateCommand(); read.Transaction = transaction;
                read.CommandText = "SELECT at FROM day_notes WHERE date=$d"; read.Parameters.AddWithValue("$d", key);
                if (read.ExecuteScalar() is long mine && mine >= note.At) continue;
                using var write = connection.CreateCommand(); write.Transaction = transaction;
                write.CommandText = "INSERT OR REPLACE INTO day_notes VALUES ($d,$t,$a)";
                write.Parameters.AddWithValue("$d", key); write.Parameters.AddWithValue("$t", DayNote.Clean(note.Text)); write.Parameters.AddWithValue("$a", note.At);
                write.ExecuteNonQuery(); changed = true;
            }
            transaction.Commit();
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { return false; }
        return changed;
    }

    /// <summary>Writes a note made on this PC, newer than any copy (empty text deletes it).</summary>
    public bool SetNote(DateTime date, string text, DateTimeOffset now)
    {
        var existing = Notes(10_000).FirstOrDefault(n => n.Date == date.Date)?.At ?? 0;
        return MergeNotes([new DayNote(date.Date, DayNote.Clean(text), Math.Max(now.ToUnixTimeMilliseconds(), existing + 1))], now);
    }

    /// <summary>Streams every reading of one window, oldest first, without holding them all in memory.</summary>
    public void ForEach(string provider, string account, string window, Action<UsagePoint> visit)
    {
        try
        {
            using var connection = Open(); using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT at,used,reset,period FROM observations WHERE provider=$p AND account=$a AND window=$w ORDER BY at";
            cmd.Parameters.AddWithValue("$p", provider); cmd.Parameters.AddWithValue("$a", account); cmd.Parameters.AddWithValue("$w", window);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) visit(new(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)), reader.GetDouble(1),
                reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)), reader.GetString(3)));
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { Error = "History is temporarily unavailable. Live usage still works."; }
    }

    public IReadOnlyList<UsageEvent> ReadEvents(string provider, string account, string window, DateTimeOffset since)
    {
        var events = new List<UsageEvent>();
        try
        {
            using var connection = Open(); using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT at,kind,explanation FROM history_events WHERE provider=$p AND account=$a AND (window=$w OR window='') AND at >= $s ORDER BY at";
            cmd.Parameters.AddWithValue("$p", provider); cmd.Parameters.AddWithValue("$a", account);
            cmd.Parameters.AddWithValue("$w", window); cmd.Parameters.AddWithValue("$s", since.ToUnixTimeMilliseconds());
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) events.Add(new(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)), reader.GetString(1), reader.GetString(2)));
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { Error = "History events are temporarily unavailable."; }
        return events;
    }

    // Aggregate in SQLite for all-history navigation; raw readings remain untouched.
    public IReadOnlyList<UsagePoint> ReadOverview(string provider, string account, string window)
    {
        var points = new List<UsagePoint>();
        try
        {
            using var connection = Open(); using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                WITH bounds AS (SELECT MIN(at) lo, MAX(at) hi FROM observations WHERE provider=$p AND account=$a AND window=$w),
                ranked AS (SELECT at,used,reset,period,
                    ROW_NUMBER() OVER (PARTITION BY (at-lo)/MAX(1,(hi-lo)/800) ORDER BY at) first,
                    ROW_NUMBER() OVER (PARTITION BY (at-lo)/MAX(1,(hi-lo)/800) ORDER BY at DESC) last,
                    ROW_NUMBER() OVER (PARTITION BY (at-lo)/MAX(1,(hi-lo)/800) ORDER BY used) low,
                    ROW_NUMBER() OVER (PARTITION BY (at-lo)/MAX(1,(hi-lo)/800) ORDER BY used DESC) high
                    FROM observations,bounds WHERE provider=$p AND account=$a AND window=$w)
                SELECT at,used,reset,period FROM ranked WHERE first=1 OR last=1 OR low=1 OR high=1 ORDER BY at;
                """;
            cmd.Parameters.AddWithValue("$p", provider); cmd.Parameters.AddWithValue("$a", account); cmd.Parameters.AddWithValue("$w", window);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) points.Add(new(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)), reader.GetDouble(1),
                reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)), reader.GetString(3)));
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { Error = "History overview is temporarily unavailable."; }
        return points;
    }

    public string FilePath => _path;
    public long Bytes() { try { var f = new FileInfo(_path); return f.Exists ? f.Length : 0; } catch { return 0; } }

    /// <summary>How many readings and events are stored, and the oldest reading.</summary>
    public (long Readings, long Events, DateTimeOffset? Since) Counts()
    {
        try
        {
            using var connection = Open(); using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT (SELECT COUNT(*) FROM observations), (SELECT COUNT(*) FROM history_events), (SELECT MIN(at) FROM observations)";
            using var r = cmd.ExecuteReader(); r.Read();
            return (r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)));
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { return (0, 0, null); }
    }

    /// <summary>Deletes every reading, event and saved provider state, then reclaims the space. Cannot be undone.</summary>
    public bool DeleteAll()
    {
        try
        {
            using var connection = Open(); using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM observations; DELETE FROM history_events; DELETE FROM provider_state; DELETE FROM plan_history; DELETE FROM day_notes;";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "VACUUM"; cmd.ExecuteNonQuery();
            Error = null;
            return true;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Rebuilds the database file to reclaim space. Readings are kept.</summary>
    public bool Compact()
    {
        try { using var connection = Open(); using var cmd = connection.CreateCommand(); cmd.CommandText = "VACUUM"; cmd.ExecuteNonQuery(); return true; }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Writes every reading as CSV (provider, window, ISO time, used fraction, reset). Account keys are left out.</summary>
    public int ExportCsv(string file)
    {
        using var connection = Open(); using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT provider, window, at, used, reset FROM observations ORDER BY provider, window, at";
        using var r = cmd.ExecuteReader();
        using var writer = new StreamWriter(file, false, new UTF8Encoding(false));
        writer.WriteLine("provider,window,time,used,reset");
        var rows = 0;
        while (r.Read())
        {
            var reset = r.IsDBNull(4) ? "" : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)).ToString("o");
            writer.WriteLine($"{r.GetString(0)},{r.GetString(1)},{DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)):o},{r.GetDouble(3).ToString(System.Globalization.CultureInfo.InvariantCulture)},{reset}");
            rows++;
        }
        return rows;
    }
}
