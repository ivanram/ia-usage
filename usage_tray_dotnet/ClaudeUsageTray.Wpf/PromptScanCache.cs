using System.Linq;

namespace ClaudeUsageTray;

/// <summary>
/// Holds each coding agent's most recent full prompt scan (per-project
/// totals and every real prompt's own timestamp) in memory, refreshed only
/// by TrayOrchestrator's existing 60-minute sampling timer — never by the
/// Stats window or the Telegram bot themselves. Those used to call
/// ClaudeCodeProjectsHelper/CodexProjectsHelper's scan methods directly on
/// every render (window open, range-tab click, /proyectos), which meant a
/// full re-read of every transcript file on disk for each interaction —
/// fine once an hour in the background, not fine on every click, which is
/// exactly what was pegging the CPU. Reading from this cache instead is
/// just in-memory filtering, however often the UI wants to re-render.
/// </summary>
public sealed class PromptScanCache
{
    public sealed record AgentScan(Dictionary<string, int> TotalsByProject, List<DateTimeOffset> Timestamps)
    {
        public static readonly AgentScan Empty = new(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), new List<DateTimeOffset>());
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, AgentScan> _byAgent = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string agent, Dictionary<string, int> totalsByProject, List<DateTimeOffset> timestamps)
    {
        lock (_lock)
        {
            _byAgent[agent] = new AgentScan(totalsByProject, timestamps);
        }
    }

    public AgentScan Get(string agent)
    {
        lock (_lock)
        {
            return _byAgent.TryGetValue(agent, out var scan) ? scan : AgentScan.Empty;
        }
    }

    public int TotalPromptCount(string agent) => Get(agent).TotalsByProject.Values.Sum();

    public int PromptCountInRange(string agent, DateTimeOffset since, DateTimeOffset? until)
    {
        var timestamps = Get(agent).Timestamps;
        var count = 0;
        foreach (var at in timestamps)
        {
            if (at >= since && (!until.HasValue || at < until.Value)) count++;
        }
        return count;
    }
}
