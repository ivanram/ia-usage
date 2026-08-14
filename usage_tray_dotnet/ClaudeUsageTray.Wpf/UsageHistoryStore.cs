using System.IO;
using Microsoft.Data.Sqlite;

namespace ClaudeUsageTray;

public readonly record struct UsageHistoryPoint(DateTimeOffset RecordedAt, int Percent);

/// <summary>
/// One row per (service, refresh) sample of that service's primary
/// (weekly-quota-equivalent) bar — a plain SQLite file rather than
/// anything heavier, since this is a single-user, single-process append
/// mostly-read log that never needs concurrent writers. Foundation for the
/// stats window's timeline chart and, later, weekly/monthly rollups; the
/// schema only records raw (timestamp, percent) pairs so any future
/// aggregation is just a different query over the same data, not a
/// migration.
/// </summary>
public sealed class UsageHistoryStore
{
    private readonly string _connectionString;

    public UsageHistoryStore()
    {
        var dbPath = Path.Combine(Paths.AppDataDir, "history.db");
        _connectionString = $"Data Source={dbPath}";
        Initialize();
    }

    /// <summary>
    /// Every OTHER method in this class already treats a SQLite failure as
    /// "history is a nice-to-have" and swallows it — this one didn't, and
    /// it runs from the constructor, which TrayOrchestrator's own field
    /// initializer calls before anything else (tray icon, popup, menu) gets
    /// a chance to exist. An unhandled exception here used to take the
    /// entire app down before a single pixel got drawn: confirmed for real
    /// via a DllNotFoundException on e_sqlite3 (a framework-dependent build
    /// published without the native SQLite binary bundled in) — the app
    /// just never appeared, no window, no tray icon, no error, with the
    /// crash sitting silently in a log file nobody was told to look at.
    /// Same try/catch-and-degrade treatment as every sibling method now.
    /// </summary>
    private void Initialize()
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS usage_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    recorded_at TEXT NOT NULL,
                    service TEXT NOT NULL,
                    percent INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_usage_history_service_time ON usage_history(service, recorded_at);

                CREATE TABLE IF NOT EXISTS usage_resets (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    recorded_at TEXT NOT NULL,
                    service TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_usage_resets_service_time ON usage_resets(service, recorded_at);
                """;
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(Paths.LogsDir, "history_store_debug.txt"), $"{DateTime.Now:O} Initialize failed, history/stats will be unavailable this run:\n{ex}\n"); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// One row per detected reset of a service's PRIMARY (weekly-quota)
    /// bar — never the short-window one (Claude's 5-hour limit, say).
    /// Purely a "when did this happen" log so the Stats chart can mark it;
    /// separate from usage_history's percent samples since a reset is a
    /// discrete event, not a periodic reading.
    /// </summary>
    public void RecordReset(string service, DateTimeOffset at)
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO usage_resets (recorded_at, service) VALUES ($t, $s)";
            cmd.Parameters.AddWithValue("$t", at.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$s", service);
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // History is a nice-to-have — never let a storage hiccup affect the live refresh.
        }
    }

    public List<DateTimeOffset> GetResets(string service, DateTimeOffset since, DateTimeOffset? until = null)
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT recorded_at FROM usage_resets WHERE service = $s AND recorded_at >= $since"
                + (until.HasValue ? " AND recorded_at < $until" : "") + " ORDER BY recorded_at ASC";
            cmd.Parameters.AddWithValue("$s", service);
            cmd.Parameters.AddWithValue("$since", since.ToUniversalTime().ToString("O"));
            if (until.HasValue) cmd.Parameters.AddWithValue("$until", until.Value.ToUniversalTime().ToString("O"));
            using var reader = cmd.ExecuteReader();
            var result = new List<DateTimeOffset>();
            while (reader.Read())
            {
                result.Add(DateTimeOffset.Parse(reader.GetString(0)));
            }
            return result;
        }
        catch
        {
            return new List<DateTimeOffset>();
        }
    }

    public void Record(string service, int percent)
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO usage_history (recorded_at, service, percent) VALUES ($t, $s, $p)";
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$s", service);
            cmd.Parameters.AddWithValue("$p", percent);
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // History is a nice-to-have — never let a storage hiccup affect the live refresh.
        }
    }

    public List<UsageHistoryPoint> GetHistory(string service, DateTimeOffset since, DateTimeOffset? until = null)
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT recorded_at, percent FROM usage_history WHERE service = $s AND recorded_at >= $since"
                + (until.HasValue ? " AND recorded_at < $until" : "") + " ORDER BY recorded_at ASC";
            cmd.Parameters.AddWithValue("$s", service);
            cmd.Parameters.AddWithValue("$since", since.ToUniversalTime().ToString("O"));
            if (until.HasValue) cmd.Parameters.AddWithValue("$until", until.Value.ToUniversalTime().ToString("O"));
            using var reader = cmd.ExecuteReader();
            var result = new List<UsageHistoryPoint>();
            while (reader.Read())
            {
                result.Add(new UsageHistoryPoint(DateTimeOffset.Parse(reader.GetString(0)), reader.GetInt32(1)));
            }
            return result;
        }
        catch
        {
            return new List<UsageHistoryPoint>();
        }
    }

    /// <summary>
    /// Every distinct local calendar day that has at least one recorded
    /// usage sample, across every service — what the "Otra fecha" calendar
    /// picker blacks out everything except. DISTINCT barely shrinks the
    /// result here (each service's own recorded_at differs by microseconds
    /// even within the same refresh tick), so this is a straightforward
    /// full scan; only ever called once, when that popup opens.
    /// </summary>
    public HashSet<DateOnly> GetDaysWithData()
    {
        var result = new HashSet<DateOnly>();
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT recorded_at FROM usage_history";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var local = DateTimeOffset.Parse(reader.GetString(0)).ToLocalTime();
                result.Add(DateOnly.FromDateTime(local.Date));
            }
        }
        catch
        {
            // Best effort — an empty set just means the picker has nothing to offer.
        }
        return result;
    }
}
