using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using UsageNotch.Models;

namespace UsageNotch.Services;

public sealed record TokenTotals(long Input, long Output, long CacheRead, long CacheWrite, double? Cost)
{
    public long Total => Input + Output + CacheRead + CacheWrite;
    public static readonly TokenTotals Zero = new(0, 0, 0, 0, null);
    public TokenTotals Add(TokenTotals o) => new(Input + o.Input, Output + o.Output, CacheRead + o.CacheRead, CacheWrite + o.CacheWrite,
        Cost is null && o.Cost is null ? null : (Cost ?? 0) + (o.Cost ?? 0));
}
public sealed record TokenShare(string Name, long Total, double? Cost, double Share);
public sealed record TokenDay(DateTime Date, long Total, double? Cost);
/// <summary>One 7-day block's tokens by model: the biggest models and "Other".</summary>
public sealed record ModelWeek(DateTime Start, IReadOnlyList<TokenShare> Models);
public sealed record TokenReport(string Source, string Label, TokenTotals Today, TokenTotals Week, IReadOnlyList<TokenDay> Days,
    IReadOnlyList<TokenShare> Projects, IReadOnlyList<TokenShare> Models, long[] TodayHours, bool AllDevices = false, long? Lifetime = null, long? PeakDaily = null, int? LongestStreak = null,
    TokenTotals? Month = null, DateTime? MonthFrom = null, IReadOnlyList<ModelWeek>? ModelWeeks = null)
{
    /// <summary>The 5 biggest models over 8 weeks; the rest are "Other".</summary>
    public static IReadOnlyList<ModelWeek> Weeks(Dictionary<(int Block, string Model), long> byBlock, DateTime today)
    {
        var top = byBlock.GroupBy(e => e.Key.Model).Select(g => (Model: g.Key, Total: g.Sum(e => e.Value))).OrderByDescending(x => x.Total).Take(5).Select(x => x.Model).ToHashSet();
        var weeks = new List<ModelWeek>();
        for (var block = 7; block >= 0; block--)
        {
            var models = byBlock.Where(e => e.Key.Block == block).GroupBy(e => top.Contains(e.Key.Model) ? e.Key.Model : "Other")
                .Select(g => (Name: g.Key, Total: g.Sum(e => e.Value))).Where(x => x.Total > 0).ToList();
            var sum = models.Sum(m => m.Total);
            weeks.Add(new ModelWeek(today.AddDays(-6 - 7 * block), models.OrderBy(m => m.Name == "Other").ThenByDescending(m => m.Total)
                .Select(m => new TokenShare(m.Name, m.Total, null, sum > 0 ? (double)m.Total / sum : 0)).ToArray()));
        }
        return weeks;
    }

    /// <summary>
    /// Replaces totals with the provider's own account-wide counts (every device, not just this PC's transcripts). Projects
    /// and models stay from this PC, since the provider doesn't break them down.
    /// </summary>
    public static TokenReport WithAccount(TokenReport? local, string source, string label, AccountTokens account, DateTimeOffset now)
    {
        var today = now.ToLocalTime().Date;
        var byDate = account.Days.GroupBy(d => d.Date.Date).ToDictionary(g => g.Key, g => g.Sum(x => x.Tokens));
        var days = Enumerable.Range(0, 30).Select(i => today.AddDays(i - 29)).Select(d => new TokenDay(d, byDate.GetValueOrDefault(d), null)).ToArray();
        var todayTotal = byDate.GetValueOrDefault(today); var week = days.TakeLast(7).Sum(d => d.Total);
        var monthFrom = new DateTime(today.Year, today.Month, 1);
        var month = byDate.Where(e => e.Key >= monthFrom && e.Key <= today).Sum(e => e.Value);
        return new TokenReport(source, label, new TokenTotals(todayTotal, 0, 0, 0, null), new TokenTotals(week, 0, 0, 0, null), days,
            local?.Projects ?? [], local?.Models ?? [], local?.TodayHours ?? new long[24], true, account.Lifetime, account.PeakDaily, account.LongestStreak,
            new TokenTotals(month, 0, 0, 0, null), monthFrom, local?.ModelWeeks);
    }
}

/// <summary>
/// Token counts from the local transcripts that Claude Code (…/projects/**/*.jsonl) and Codex (…/sessions/**/*.jsonl) keep on
/// this PC. Files are read incrementally from where the last scan stopped, only usage numbers, model names and the project
/// folder name are kept, and nothing is uploaded. Cost is the API-price equivalent, not what a subscription pays.
/// </summary>
public sealed class LocalTokenUsage
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string Status { get; private set; } = "";
    public DateTimeOffset? LastScan { get; private set; }
    public event Action? Changed;

    public LocalTokenUsage(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageNotch", "tokens.db");

    public sealed record Source(string Id, string Label, string Root, bool Codex);

    public static IReadOnlyList<Source> SourcesFor(AppSettings settings)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<Source>();
        if (settings.ClaudeEnabled) list.Add(new("claude", "Claude Code", Path.Combine(profile, ".claude", "projects"), false));
        if (settings.CodexEnabled) list.Add(new("codex", "Codex", Path.Combine(profile, ".codex", "sessions"), true));
        foreach (var a in settings.Accounts.Where(a => a.Enabled && Directory.Exists(a.Folder)))
            list.Add(new(a.ProviderId, a.DisplayName, Path.Combine(a.Folder, a.Kind == "codex" ? "sessions" : "projects"), a.Kind == "codex"));
        return list;
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS files (path TEXT PRIMARY KEY, source TEXT NOT NULL, size INTEGER NOT NULL, offset INTEGER NOT NULL,
                last_id TEXT, project TEXT, model TEXT, codex_input INTEGER, codex_cached INTEGER, codex_output INTEGER);
            CREATE TABLE IF NOT EXISTS usage (source TEXT NOT NULL, project TEXT NOT NULL, model TEXT NOT NULL, hour INTEGER NOT NULL,
                input INTEGER NOT NULL, output INTEGER NOT NULL, cache_read INTEGER NOT NULL, write5 INTEGER NOT NULL, write60 INTEGER NOT NULL, messages INTEGER NOT NULL,
                PRIMARY KEY(source, project, model, hour));
            """;
        command.ExecuteNonQuery();
        return connection;
    }

    private sealed class Bucket { public long Input, Output, CacheRead, Write5, Write60, Messages; }
    private sealed class FileState { public long Size, Offset; public string? LastId, Project, Model; public long CodexInput, CodexCached, CodexOutput; }

    /// <summary>Reads what was appended since the last scan. Safe to call often; a scan in progress makes later calls return.</summary>
    public async Task ScanAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (!settings.TrackLocalTokens || !await _gate.WaitAsync(0, ct)) return;
        try { await Task.Run(() => Scan(SourcesFor(settings), ct), ct); }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { Status = "Local token history is temporarily unavailable."; }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    private void Scan(IReadOnlyList<Source> sources, CancellationToken ct)
    {
        using var db = Open();
        var known = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        using (var read = db.CreateCommand())
        {
            read.CommandText = "SELECT path,size,offset,last_id,project,model,codex_input,codex_cached,codex_output FROM files";
            using var r = read.ExecuteReader();
            while (r.Read()) known[r.GetString(0)] = new FileState { Size = r.GetInt64(1), Offset = r.GetInt64(2), LastId = r.IsDBNull(3) ? null : r.GetString(3),
                Project = r.IsDBNull(4) ? null : r.GetString(4), Model = r.IsDBNull(5) ? null : r.GetString(5),
                CodexInput = r.IsDBNull(6) ? 0 : r.GetInt64(6), CodexCached = r.IsDBNull(7) ? 0 : r.GetInt64(7), CodexOutput = r.IsDBNull(8) ? 0 : r.GetInt64(8) };
        }
        var work = new List<(Source Source, FileInfo File)>();
        foreach (var source in sources)
        {
            if (!Directory.Exists(source.Root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(source.Root, "*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8 }); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var path in files)
            {
                var info = new FileInfo(path);
                if (!known.TryGetValue(path, out var state) || state.Size != info.Length) work.Add((source, info));
            }
        }
        if (work.Count == 0) { Status = ""; LastScan = DateTimeOffset.Now; return; }
        var totalBytes = Math.Max(1, work.Sum(w => w.File.Length - (known.TryGetValue(w.File.FullName, out var s) && s.Offset <= w.File.Length ? s.Offset : 0)));
        long doneBytes = 0;
        foreach (var (source, file) in work.OrderBy(w => w.File.LastWriteTimeUtc))
        {
            ct.ThrowIfCancellationRequested();
            var state = known.GetValueOrDefault(file.FullName) ?? new FileState();
            if (state.Offset > file.Length) state = new FileState(); // Rewritten: start over (counts were already added once).
            var buckets = new Dictionary<(string Project, string Model, long Hour), Bucket>();
            long start = state.Offset;
            try { state.Offset = ReadFile(file.FullName, state, source, buckets, ct); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            state.Size = file.Length;
            doneBytes += Math.Max(0, state.Offset - start);
            using var transaction = db.BeginTransaction();
            foreach (var ((project, model, hour), b) in buckets)
            {
                using var up = db.CreateCommand(); up.Transaction = transaction;
                up.CommandText = """
                    INSERT INTO usage VALUES ($s,$p,$m,$h,$i,$o,$r,$w5,$w60,$n)
                    ON CONFLICT(source,project,model,hour) DO UPDATE SET input=input+$i, output=output+$o, cache_read=cache_read+$r, write5=write5+$w5, write60=write60+$w60, messages=messages+$n
                    """;
                up.Parameters.AddWithValue("$s", source.Id); up.Parameters.AddWithValue("$p", project); up.Parameters.AddWithValue("$m", model); up.Parameters.AddWithValue("$h", hour);
                up.Parameters.AddWithValue("$i", b.Input); up.Parameters.AddWithValue("$o", b.Output); up.Parameters.AddWithValue("$r", b.CacheRead);
                up.Parameters.AddWithValue("$w5", b.Write5); up.Parameters.AddWithValue("$w60", b.Write60); up.Parameters.AddWithValue("$n", b.Messages);
                up.ExecuteNonQuery();
            }
            using (var save = db.CreateCommand())
            {
                save.Transaction = transaction;
                save.CommandText = "INSERT OR REPLACE INTO files VALUES ($path,$s,$size,$off,$id,$p,$m,$ci,$cc,$co)";
                save.Parameters.AddWithValue("$path", file.FullName); save.Parameters.AddWithValue("$s", source.Id); save.Parameters.AddWithValue("$size", state.Size);
                save.Parameters.AddWithValue("$off", state.Offset); save.Parameters.AddWithValue("$id", (object?)state.LastId ?? DBNull.Value);
                save.Parameters.AddWithValue("$p", (object?)state.Project ?? DBNull.Value); save.Parameters.AddWithValue("$m", (object?)state.Model ?? DBNull.Value);
                save.Parameters.AddWithValue("$ci", state.CodexInput); save.Parameters.AddWithValue("$cc", state.CodexCached); save.Parameters.AddWithValue("$co", state.CodexOutput);
                save.ExecuteNonQuery();
            }
            transaction.Commit();
            Status = $"Reading local Claude Code and Codex history… {Math.Min(99, doneBytes * 100 / totalBytes)}%";
        }
        Status = ""; LastScan = DateTimeOffset.Now;
    }

    private static readonly byte[] UsageMarker = "\"usage\""u8.ToArray();
    private static readonly byte[] TokenCountMarker = "\"token_count\""u8.ToArray();
    private static readonly byte[] MetaMarker = "\"session_meta\""u8.ToArray();
    private static readonly byte[] TurnMarker = "\"turn_context\""u8.ToArray();

    /// <summary>Reads complete lines from the stored offset and returns the new offset (the end of the last complete line).</summary>
    private static long ReadFile(string path, FileState state, Source source, Dictionary<(string, string, long), Bucket> buckets, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        stream.Seek(state.Offset, SeekOrigin.Begin);
        var pending = new ArrayBufferWriter<byte>(1 << 16);
        var chunk = ArrayPool<byte>.Shared.Rent(1 << 20);
        var offset = state.Offset;
        var recentIds = new Queue<string>(); var seen = new HashSet<string>();
        if (state.LastId is { } last) { seen.Add(last); recentIds.Enqueue(last); }
        try
        {
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                var span = chunk.AsSpan(0, read);
                while (true)
                {
                    var newline = span.IndexOf((byte)'\n');
                    if (newline < 0) { if (pending.WrittenCount + span.Length < 64 << 20) pending.Write(span); else pending.Clear(); break; }
                    ReadOnlySpan<byte> line;
                    byte[]? joined = null;
                    if (pending.WrittenCount > 0) { pending.Write(span[..newline]); joined = pending.WrittenSpan.ToArray(); line = joined; pending.Clear(); }
                    else line = span[..newline];
                    offset += line.Length + 1;
                    if (source.Codex) CodexLine(line, state, source, buckets);
                    else ClaudeLine(line, state, buckets, seen, recentIds);
                    span = span[(newline + 1)..];
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(chunk); }
        return offset;
    }

    private static Bucket BucketFor(Dictionary<(string, string, long), Bucket> buckets, string project, string model, DateTimeOffset at)
    {
        var key = (project, model, at.ToUnixTimeSeconds() / 3600);
        if (!buckets.TryGetValue(key, out var b)) buckets[key] = b = new Bucket();
        return b;
    }

    private static string ProjectName(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return "Other";
        var name = Path.GetFileName(cwd.TrimEnd('\\', '/'));
        return string.IsNullOrWhiteSpace(name) ? "Other" : new string(name.Where(c => !char.IsControl(c)).Take(60).ToArray());
    }

    private static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n > 0 ? n : 0;

    private static void ClaudeLine(ReadOnlySpan<byte> line, FileState state, Dictionary<(string, string, long), Bucket> buckets, HashSet<string> seen, Queue<string> recent)
    {
        if (line.IndexOf(UsageMarker) < 0) return;
        try
        {
            using var doc = JsonDocument.Parse(line.ToArray());
            var root = doc.RootElement;
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return;
            if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return;
            if (message.TryGetProperty("role", out var role) && role.GetString() != "assistant") return;
            var model = message.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
            if (model.Length == 0 || model.StartsWith('<')) return;
            // Claude Code writes one line per content block of a message, each repeating the same usage.
            var messageId = message.TryGetProperty("id", out var mid) ? mid.GetString() : null;
            var requestId = root.TryGetProperty("requestId", out var rid) ? rid.GetString() : null;
            if (messageId is not null || requestId is not null)
            {
                var id = messageId + "|" + requestId;
                if (!seen.Add(id)) return;
                recent.Enqueue(id); if (recent.Count > 512) seen.Remove(recent.Dequeue());
                state.LastId = id;
            }
            if (!root.TryGetProperty("timestamp", out var ts) || !DateTimeOffset.TryParse(ts.GetString(), out var at)) return;
            var project = ProjectName(root.TryGetProperty("cwd", out var cwd) ? cwd.GetString() : null);
            var b = BucketFor(buckets, project, model, at);
            b.Input += Long(usage, "input_tokens"); b.Output += Long(usage, "output_tokens"); b.CacheRead += Long(usage, "cache_read_input_tokens");
            var write = Long(usage, "cache_creation_input_tokens");
            if (usage.TryGetProperty("cache_creation", out var creation) && creation.ValueKind == JsonValueKind.Object)
            {
                var hour = Long(creation, "ephemeral_1h_input_tokens"); b.Write60 += hour; b.Write5 += Math.Max(0, write - hour);
            }
            else b.Write5 += write;
            b.Messages++;
        }
        catch (JsonException) { }
    }

    private static void CodexLine(ReadOnlySpan<byte> line, FileState state, Source source, Dictionary<(string, string, long), Bucket> buckets)
    {
        var tokens = line.IndexOf(TokenCountMarker) >= 0;
        if (!tokens && line.IndexOf(MetaMarker) < 0 && line.IndexOf(TurnMarker) < 0) return;
        try
        {
            using var doc = JsonDocument.Parse(line.ToArray());
            var root = doc.RootElement;
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "session_meta") { if (payload.TryGetProperty("cwd", out var cwd)) state.Project = ProjectName(cwd.GetString()); return; }
            if (type == "turn_context") { if (payload.TryGetProperty("model", out var model)) state.Model = model.GetString(); return; }
            if (!tokens || !payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) return;
            if (!info.TryGetProperty("total_token_usage", out var total) || total.ValueKind != JsonValueKind.Object) return;
            // Totals are cumulative and repeat between turns; only increases are new usage.
            long input = Long(total, "input_tokens"), cached = Long(total, "cached_input_tokens"), output = Long(total, "output_tokens");
            if (input + output <= state.CodexInput + state.CodexOutput) return;
            var dInput = Math.Max(0, input - state.CodexInput); var dCached = Math.Max(0, cached - state.CodexCached); var dOutput = Math.Max(0, output - state.CodexOutput);
            state.CodexInput = input; state.CodexCached = cached; state.CodexOutput = output;
            if (!root.TryGetProperty("timestamp", out var ts) || !DateTimeOffset.TryParse(ts.GetString(), out var at)) return;
            var b = BucketFor(buckets, state.Project ?? "Other", state.Model ?? "codex", at);
            b.CacheRead += Math.Min(dCached, dInput); b.Input += Math.Max(0, dInput - dCached); b.Output += dOutput; b.Messages++;
        }
        catch (JsonException) { }
    }

    /// <summary>Totals for today, the last 7 days and 30 days of one source, in local time.</summary>
    public TokenReport? Report(Source source, DateTimeOffset now, IReadOnlySet<string>? keep = null)
    {
        try
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT project,model,hour,input,output,cache_read,write5,write60 FROM usage WHERE source=$s AND hour >= $h";
            cmd.Parameters.AddWithValue("$s", source.Id);
            var today = now.ToLocalTime().Date; var weekFrom = today.AddDays(-6);
            var monthFrom = new DateTime(today.Year, today.Month, 1); var blocksFrom = today.AddDays(-55);
            var since = new DateTimeOffset(monthFrom < blocksFrom ? monthFrom : blocksFrom);
            cmd.Parameters.AddWithValue("$h", Math.Min(now.AddDays(-31).ToUnixTimeSeconds(), since.ToUnixTimeSeconds()) / 3600);
            var month = TokenTotals.Zero; var byBlock = new Dictionary<(int, string), long>();
            var days = new Dictionary<DateTime, TokenTotals>(); var projects = new Dictionary<string, TokenTotals>(); var models = new Dictionary<string, TokenTotals>();
            var todayTotals = TokenTotals.Zero; var week = TokenTotals.Zero; var hours = new long[24]; var any = false;
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var model = r.GetString(1); var local = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(2) * 3600).ToLocalTime();
                    var totals = ModelPricing.Totals(model, r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), source.Codex);
                    if (local.Date >= monthFrom && local.Date <= today) month = month.Add(totals);
                    if (local.Date >= blocksFrom && local.Date <= today) { var key = ((today - local.Date).Days / 7, model); byBlock[key] = byBlock.GetValueOrDefault(key) + totals.Total; }
                    if (local.Date < today.AddDays(-30)) continue;
                    any = true;
                    days[local.Date] = days.GetValueOrDefault(local.Date, TokenTotals.Zero).Add(totals);
                    if (local.Date == today) { todayTotals = todayTotals.Add(totals); hours[local.Hour] += totals.Total; }
                    if (local.Date >= weekFrom)
                    {
                        week = week.Add(totals);
                        projects[r.GetString(0)] = projects.GetValueOrDefault(r.GetString(0), TokenTotals.Zero).Add(totals);
                        models[model] = models.GetValueOrDefault(model, TokenTotals.Zero).Add(totals);
                    }
                }
            if (!any) return null;
            // The biggest projects, plus any project with a budget, so its budget can always be checked.
            IReadOnlyList<TokenShare> Top(Dictionary<string, TokenTotals> map, int count) => map.OrderByDescending(e => e.Value.Total).Where((e, i) => i < count || keep?.Contains(e.Key) == true)
                .Select(e => new TokenShare(e.Key, e.Value.Total, e.Value.Cost, week.Total > 0 ? (double)e.Value.Total / week.Total : 0)).ToArray();
            var series = Enumerable.Range(0, 30).Select(i => today.AddDays(i - 29))
                .Select(d => days.TryGetValue(d, out var v) ? new TokenDay(d, v.Total, v.Cost) : new TokenDay(d, 0, source.Codex ? null : 0)).ToArray();
            return new TokenReport(source.Id, source.Label, todayTotals, week, series, Top(projects, 8), Top(models, 6), hours,
                Month: month, MonthFrom: monthFrom, ModelWeeks: TokenReport.Weeks(byBlock, today));
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { return null; }
    }

    public IReadOnlyList<TokenReport> Reports(AppSettings settings, DateTimeOffset now) =>
        !settings.TrackLocalTokens ? [] : SourcesFor(settings).Select(s => Report(s, now, settings.ProjectBudgets.Keys.Where(k => k.StartsWith(s.Id + "|")).Select(k => k[(s.Id.Length + 1)..]).ToHashSet()))
            .Where(r => r is not null).Select(r => r!).ToArray();

    public long DatabaseBytes() { try { return new FileInfo(_path).Exists ? new FileInfo(_path).Length : 0; } catch { return 0; } }

    /// <summary>Forgets every count and file position; the next scan reads all transcripts again.</summary>
    public void Reset()
    {
        try { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM usage; DELETE FROM files; VACUUM;"; cmd.ExecuteNonQuery(); }
        catch (Exception e) when (e is SqliteException or IOException) { }
        Changed?.Invoke();
    }
}

/// <summary>
/// Anthropic first-party API prices per million tokens (input, output, cache read), from the published price list. Cache
/// writes cost 1.25× input for 5 minutes and 2× for 1 hour. Unknown models have no cost rather than a guessed one.
/// </summary>
public static class ModelPricing
{
    private static readonly (string Prefix, double Input, double Output, double CacheRead)[] Claude =
    [
        ("claude-fable-5-1", 10, 50, .25), ("claude-mythos-5-1", 10, 50, .25), ("claude-fable-5", 10, 50, 1), ("claude-mythos-5", 10, 50, 1),
        ("claude-opus-5-5", 4, 20, .2), ("claude-opus-5", 5, 25, .5), ("claude-opus-4-8", 5, 25, .5), ("claude-opus-4-7", 5, 25, .5),
        ("claude-opus-4-6", 5, 25, .5), ("claude-opus-4-5", 5, 25, .5), ("claude-opus-4", 15, 75, 1.5),
        ("claude-sonnet-5-5", 2, 10, .2), ("claude-sonnet-5", 2, 10, .2), ("claude-sonnet-4", 3, 15, .3), ("claude-3-7-sonnet", 3, 15, .3),
        ("claude-haiku-5-5", .1, .5, .01), ("claude-haiku-4-5", 1, 5, .1), ("claude-3-5-haiku", .8, 4, .08),
    ];

    public static (double Input, double Output, double CacheRead)? Price(string model)
    {
        foreach (var p in Claude) if (model.StartsWith(p.Prefix, StringComparison.OrdinalIgnoreCase)) return (p.Input, p.Output, p.CacheRead);
        return null;
    }

    public static TokenTotals Totals(string model, long input, long output, long cacheRead, long write5, long write60, bool codex)
    {
        double? cost = null;
        if (!codex && Price(model) is { } p)
            cost = (input * p.Input + output * p.Output + cacheRead * p.CacheRead + write5 * p.Input * 1.25 + write60 * p.Input * 2) / 1_000_000;
        return new TokenTotals(input, output, cacheRead, write5 + write60, cost);
    }
}
