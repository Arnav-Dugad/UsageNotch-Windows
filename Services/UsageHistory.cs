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
}
