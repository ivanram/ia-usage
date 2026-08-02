using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeUsageTray;

/// <summary>
/// Answers "what's actually in use on this PC right now" for the Telegram
/// /apps command — Windows has no direct API for "list of open apps", so
/// this uses the same rough definition Task Manager's own Apps tab does: a
/// process with a real, titled top-level window. CPU% comes from diffing
/// TotalProcessorTime over a short sampling window, the standard technique
/// for per-process CPU without setting up performance counters/ETW (which
/// would need more than this app should require just to answer a chat
/// command).
/// </summary>
internal static class RunningAppsHelper
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public readonly record struct RunningApp(string Name, string WindowTitle, bool IsForeground, bool IsActive, double CpuPercent);

    // Substring match against process name or window title — deliberately
    // broad (catches "ChatGPT", "OpenAI ChatGPT", a browser tab titled
    // "Grok - X", etc.) rather than an exact allow-list of exe names, since
    // most of these are PWAs/browser tabs with no fixed process name.
    private static readonly string[] AiKeywords =
    {
        "claude", "chatgpt", "openai", "gpt", "grok", "gemini", "copilot", "perplexity",
    };

    public static bool IsAiApp(RunningApp app) =>
        AiKeywords.Any(k => app.Name.Contains(k, StringComparison.OrdinalIgnoreCase) || app.WindowTitle.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Snapshot of every windowed app, each flagged as foreground and/or
    /// "active" (CPU% at or above <paramref name="cpuActiveThresholdPercent"/>
    /// during the sample). Ordered foreground-first, then busiest-first, so
    /// the most relevant apps land at the top of a Telegram message without
    /// needing to scroll.
    /// </summary>
    public static async Task<List<RunningApp>> GetRunningAppsAsync(int cpuActiveThresholdPercent = 3, int sampleWindowMs = 400)
    {
        var foregroundPid = GetForegroundProcessId();
        var currentPid = Environment.ProcessId;

        var candidates = Process.GetProcesses()
            .Where(p => p.Id != currentPid)
            .Where(p =>
            {
                try { return p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle); }
                catch { return false; }
            })
            .ToList();

        var before = candidates.ToDictionary(p => p.Id, TryGetCpuTime);

        await Task.Delay(sampleWindowMs);

        var cpuCount = Math.Max(1, Environment.ProcessorCount);
        var result = new List<RunningApp>();
        foreach (var p in candidates)
        {
            try
            {
                var t0 = before[p.Id];
                var t1 = TryGetCpuTime(p);
                var cpuPercent = t0 is { } a && t1 is { } b
                    ? Math.Max(0, (b - a).TotalMilliseconds / (sampleWindowMs * cpuCount) * 100.0)
                    : 0;

                result.Add(new RunningApp(
                    Name: p.ProcessName,
                    WindowTitle: p.MainWindowTitle,
                    IsForeground: p.Id == foregroundPid,
                    IsActive: cpuPercent >= cpuActiveThresholdPercent,
                    CpuPercent: cpuPercent));
            }
            catch
            {
                // Exited mid-sample, or access denied (elevated process) — skip it.
            }
            finally
            {
                p.Dispose();
            }
        }

        // AI apps sort first, full stop — the whole point of this app is
        // tracking AI usage, so those are what the user actually opened
        // /apps to check on, not whatever happens to be in the foreground.
        return result
            .OrderByDescending(IsAiApp)
            .ThenByDescending(a => a.IsForeground)
            .ThenByDescending(a => a.CpuPercent)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static TimeSpan? TryGetCpuTime(Process p)
    {
        try { return p.TotalProcessorTime; }
        catch { return null; }
    }

    private static int GetForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return -1;
        GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }
}
