using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using UsageNotch.Models;

namespace UsageNotch.Services;

public sealed record UsagePoint(DateTimeOffset At, double Used, DateTimeOffset? Reset, string Period);

/// <summary>Local, bounded observations. Never stores credentials, names, emails or provider error text.</summary>
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
            """;
        command.ExecuteNonQuery();
        return connection;
    }

    public void Record(ProviderSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.Status != SnapshotStatus.Ok || snapshot.FetchedAt is not { } at || at > now.AddMinutes(1) || now - at > TimeSpan.FromMinutes(15)) return;
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            foreach (var window in snapshot.Windows)
            {
                if (window.UsedFraction is not { } used || !double.IsFinite(used) || used < 0) continue;
                var account = AccountKey(snapshot);
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
                        if (at - lastAt <= TimeSpan.FromMinutes(20) && used >= reader.GetDouble(1) - .0001 && reset == window.ResetsAt?.ToUnixTimeMilliseconds()
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
            using var prune = connection.CreateCommand();
            prune.Transaction = transaction;
            prune.CommandText = "DELETE FROM observations WHERE at < $cutoff";
            prune.Parameters.AddWithValue("$cutoff", now.AddDays(-30).ToUnixTimeMilliseconds());
            prune.ExecuteNonQuery();
            transaction.Commit();
            Error = null;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { Error = "History is temporarily unavailable. Live usage still works."; }
    }

    public IReadOnlyList<UsagePoint> Read(string provider, string account, string window, DateTimeOffset since)
    {
        var points = new List<UsagePoint>();
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT at,used,reset,period FROM observations WHERE provider=$p AND account=$a AND window=$w AND at >= $s ORDER BY at";
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
}
