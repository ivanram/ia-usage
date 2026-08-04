using System.IO;
using Microsoft.Data.Sqlite;

namespace ClaudeUsageTray;

public readonly record struct PromptCountSample(DateTimeOffset RecordedAt, int Total);

/// <summary>
/// Stores periodic (every 30 min — see TrayOrchestrator's sampling timer)
/// snapshots of each coding agent's CUMULATIVE prompt count per project,
/// sourced from ClaudeCodeProjectsHelper/CodexProjectsHelper's own
/// GetPromptCountsByProject(). Raw cumulative totals are kept (not
/// pre-computed deltas) so any two points in time can be diffed later —
/// what actually matters for stats is "how many NEW prompts since the last
/// check", which only makes sense as a difference between two samples.
///
/// Every sampling tick writes one row per (agent, project) it currently
/// sees, all sharing the same recorded_at — that shared timestamp is what
/// lets GetAgentDeltaSeries group rows back into "one aggregate total per
/// tick" without needing a separate table for tick metadata.
/// </summary>
public sealed class PromptCountStore
{
    private readonly string _connectionString;

    public PromptCountStore()
    {
        var dbPath = Path.Combine(Paths.AppDataDir, "history.db");
        _connectionString = $"Data Source={dbPath}";
        Initialize();
    }

    private void Initialize()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS prompt_counts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                recorded_at TEXT NOT NULL,
                agent TEXT NOT NULL,
                project TEXT NOT NULL,
                total_count INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_prompt_counts_agent_time ON prompt_counts(agent, recorded_at);
            CREATE INDEX IF NOT EXISTS idx_prompt_counts_agent_project_time ON prompt_counts(agent, project, recorded_at);
            """;
        cmd.ExecuteNonQuery();
    }

    public void RecordSnapshot(string agent, IReadOnlyDictionary<string, int> totalsByProject, DateTimeOffset at)
    {
        if (totalsByProject.Count == 0) return;
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            // Prompt counts are cumulative scans of append-only
            // transcripts — a project's total can only ever grow. A
            // transient read hiccup (a file briefly locked by
            // antivirus/cloud-sync/the Windows Search indexer) can still
            // make a single scan undercount a project despite the retries
            // in ClaudeCodeProjectsHelper/CodexProjectsHelper; without this
            // clamp that bad low reading gets stored as fact, and the next
            // normal scan's real total then looks like a burst of
            // brand-new prompts that were never typed. Clamping to the
            // highest total ever recorded for that project makes a bad
            // reading a no-op instead of corrupting the delta math
            // everything downstream (dashboard cards, charts) relies on.
            var previousMax = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (var maxCmd = conn.CreateCommand())
            {
                maxCmd.CommandText = "SELECT project, MAX(total_count) FROM prompt_counts WHERE agent = $a GROUP BY project";
                maxCmd.Parameters.AddWithValue("$a", agent);
                using var maxReader = maxCmd.ExecuteReader();
                while (maxReader.Read())
                {
                    previousMax[maxReader.GetString(0)] = maxReader.GetInt32(1);
                }
            }

            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO prompt_counts (recorded_at, agent, project, total_count) VALUES ($t, $a, $p, $c)";
            var tParam = cmd.Parameters.Add("$t", SqliteType.Text);
            var aParam = cmd.Parameters.Add("$a", SqliteType.Text);
            var pParam = cmd.Parameters.Add("$p", SqliteType.Text);
            var cParam = cmd.Parameters.Add("$c", SqliteType.Integer);

            var timestamp = at.ToUniversalTime().ToString("O");
            foreach (var (project, count) in totalsByProject)
            {
                var clamped = previousMax.TryGetValue(project, out var max) ? Math.Max(count, max) : count;
                tParam.Value = timestamp;
                aParam.Value = agent;
                pParam.Value = project;
                cParam.Value = clamped;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch
        {
            // History is a nice-to-have — never let a storage hiccup affect the sampling loop.
        }
    }

    /// <summary>
    /// True if no sample has ever been recorded, or the most recent one
    /// (across every agent — one shared cadence, not per-agent) is already
    /// older than <paramref name="minInterval"/>. Each sample is a real
    /// full-transcript disk scan, so this guards against the app being
    /// closed and reopened a few times inside the same hour clustering
    /// several near-duplicate samples together instead of the one-per-hour
    /// cadence the sampling timer is meant to keep.
    /// </summary>
    public bool ShouldSampleNow(TimeSpan minInterval)
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MAX(recorded_at) FROM prompt_counts";
            var result = cmd.ExecuteScalar();
            if (result is null or DBNull) return true;
            var last = DateTimeOffset.Parse((string)result);
            return DateTimeOffset.UtcNow - last >= minInterval;
        }
        catch
        {
            return true; // Best effort — err toward sampling rather than silently never sampling again.
        }
    }

    /// <summary>The latest known cumulative total per project — what /proyectos shows, refreshed at most every sampling tick.</summary>
    public Dictionary<string, int> GetLatestTotalsByProject(string agent)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            // For each project, the row from its own most recent recorded_at.
            cmd.CommandText = """
                SELECT project, total_count FROM prompt_counts p1
                WHERE agent = $a AND recorded_at = (
                    SELECT MAX(recorded_at) FROM prompt_counts p2 WHERE p2.agent = p1.agent AND p2.project = p1.project
                )
                """;
            cmd.Parameters.AddWithValue("$a", agent);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result[reader.GetString(0)] = reader.GetInt32(1);
            }
        }
        catch
        {
            // Best effort — an empty dictionary just means /proyectos shows no counts yet.
        }
        return result;
    }

    /// <summary>
    /// One aggregate (summed across every project) total per sampling
    /// tick, since <paramref name="since"/>. Includes one extra tick right
    /// before <paramref name="since"/> when available, purely so the very
    /// first delta inside the requested range can still be computed
    /// against something — without it the first tick after `since` would
    /// have no earlier point to diff against and would have to be dropped.
    /// </summary>
    private List<PromptCountSample> GetAggregateSeries(string agent, DateTimeOffset since, DateTimeOffset? until = null)
    {
        var result = new List<PromptCountSample>();
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            // The one tick immediately before `since`, if any — establishes
            // a baseline so the first in-range delta isn't dropped.
            using (var baselineCmd = conn.CreateCommand())
            {
                baselineCmd.CommandText = """
                    SELECT recorded_at, SUM(total_count) FROM prompt_counts
                    WHERE agent = $a AND recorded_at = (
                        SELECT MAX(recorded_at) FROM prompt_counts WHERE agent = $a AND recorded_at < $since
                    )
                    GROUP BY recorded_at
                    """;
                baselineCmd.Parameters.AddWithValue("$a", agent);
                baselineCmd.Parameters.AddWithValue("$since", since.ToUniversalTime().ToString("O"));
                using var reader = baselineCmd.ExecuteReader();
                if (reader.Read())
                {
                    result.Add(new PromptCountSample(DateTimeOffset.Parse(reader.GetString(0)), reader.GetInt32(1)));
                }
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT recorded_at, SUM(total_count) FROM prompt_counts
                WHERE agent = $a AND recorded_at >= $since{(until.HasValue ? " AND recorded_at < $until" : "")}
                GROUP BY recorded_at
                ORDER BY recorded_at ASC
                """;
            cmd.Parameters.AddWithValue("$a", agent);
            cmd.Parameters.AddWithValue("$since", since.ToUniversalTime().ToString("O"));
            if (until.HasValue) cmd.Parameters.AddWithValue("$until", until.Value.ToUniversalTime().ToString("O"));
            using var mainReader = cmd.ExecuteReader();
            while (mainReader.Read())
            {
                result.Add(new PromptCountSample(DateTimeOffset.Parse(mainReader.GetString(0)), mainReader.GetInt32(1)));
            }
        }
        catch
        {
            return new List<PromptCountSample>();
        }
        return result;
    }

    /// <summary>
    /// (timestamp, new prompts made in the interval ending at that
    /// timestamp) pairs — the actual chart-overlay/dashboard-sum data.
    /// Negative diffs (a project's history got pruned/reset between ticks)
    /// are clamped to 0 rather than shown as "negative prompts".
    /// </summary>
    public List<(DateTimeOffset At, int Delta)> GetAgentDeltaSeries(string agent, DateTimeOffset since, DateTimeOffset? until = null)
    {
        var series = GetAggregateSeries(agent, since, until);
        var result = new List<(DateTimeOffset, int)>();
        for (var i = 1; i < series.Count; i++)
        {
            var delta = Math.Max(0, series[i].Total - series[i - 1].Total);
            if (series[i].RecordedAt >= since) result.Add((series[i].RecordedAt, delta));
        }
        return result;
    }

    /// <summary>Total new prompts across the whole range — just the delta series summed.</summary>
    public int GetAgentTotalInRange(string agent, DateTimeOffset since, DateTimeOffset? until = null) =>
        GetAgentDeltaSeries(agent, since, until).Sum(d => d.Delta);

    /// <summary>
    /// (timestamp, cumulative total as of that timestamp) pairs — the raw
    /// numbers themselves rather than the differences between them, for
    /// the Stats window's "Totales" view of the prompt-count overlay.
    /// </summary>
    public List<(DateTimeOffset At, int Total)> GetAgentTotalSeries(string agent, DateTimeOffset since, DateTimeOffset? until = null) =>
        GetAggregateSeries(agent, since, until)
            .Where(s => s.RecordedAt >= since)
            .Select(s => (s.RecordedAt, s.Total))
            .ToList();
}
