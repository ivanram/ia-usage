using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace ClaudeUsageTray;

/// <summary>
/// Reads Claude Code's own local session history (%USERPROFILE%\.claude\)
/// for the Telegram /proyectos command — no web session, no API, just what
/// Claude Code itself already writes to this machine. Never reads any
/// actual conversation content — only folder names, file timestamps, and
/// the small per-instance registry entries described below.
/// </summary>
internal static class ClaudeCodeProjectsHelper
{
    private const string AgentName = "Claude Code";

    // A line this long this early in a transcript is never the small
    // metadata line carrying "cwd" — skip parsing it rather than pay for a
    // JsonDocument.Parse over a huge tool-output line.
    private const int MaxProbeLineLength = 20_000;
    private const int MaxLinesProbed = 30;

    private static string ClaudeRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    private static string ProjectsRoot => Path.Combine(ClaudeRoot, "projects");
    private static string SessionsRoot => Path.Combine(ClaudeRoot, "sessions");

    /// <summary>One entry per session file (one chat), not aggregated by project — the caller groups by ProjectPath itself.</summary>
    public static List<AgentTask> GetRecentTasks(int maxTasks = 100)
    {
        var root = ProjectsRoot;
        if (!Directory.Exists(root)) return new List<AgentTask>();

        // Live sessions are keyed by their session ID (the .jsonl file's own
        // basename) rather than cwd, so two sessions in the same folder
        // never get confused with each other.
        var liveSessions = GetLiveSessionsBySessionId();

        var result = new List<AgentTask>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            List<string> sessionFiles;
            try { sessionFiles = Directory.EnumerateFiles(dir, "*.jsonl").ToList(); }
            catch { continue; }

            foreach (var file in sessionFiles)
            {
                DateTime mtime;
                try { mtime = File.GetLastWriteTime(file); }
                catch { continue; }

                var sessionId = Path.GetFileNameWithoutExtension(file);
                var isLive = liveSessions.TryGetValue(sessionId, out var live);
                var path = isLive ? live.Cwd : (TryReadCwd(file) ?? new DirectoryInfo(dir).Name);

                result.Add(new AgentTask(AgentName, path, mtime, isLive, isLive ? live.Name : null));
            }
        }

        return result.OrderByDescending(t => t.LastActivity).Take(maxTasks).ToList();
    }

    /// <summary>
    /// Claude Code keeps one small registry file per running instance at
    /// ~/.claude/sessions/{pid}.json — sessionId, cwd, a short derived
    /// name, etc. Far more reliable than guessing "still active" from
    /// transcript write timestamps, but the file can outlive a
    /// crashed/killed process until Claude Code's own cleanup pass runs, so
    /// each entry's PID is checked against actually-running processes
    /// before it's trusted.
    /// </summary>
    private static Dictionary<string, (string? Name, string Cwd, int Pid)> GetLiveSessionsBySessionId()
    {
        var result = new Dictionary<string, (string?, string, int)>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(SessionsRoot)) return result;

        foreach (var file in Directory.EnumerateFiles(SessionsRoot, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                if (!root.TryGetProperty("pid", out var pidProp)
                    || !root.TryGetProperty("cwd", out var cwdProp)
                    || !root.TryGetProperty("sessionId", out var sessionIdProp)) continue;
                if (cwdProp.GetString() is not { Length: > 0 } cwd) continue;
                if (sessionIdProp.GetString() is not { Length: > 0 } sessionId) continue;

                var pid = pidProp.GetInt32();
                try { Process.GetProcessById(pid); }
                catch (ArgumentException) { continue; } // stale entry — process no longer running

                var name = root.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                    ? nameProp.GetString()
                    : null;
                result[sessionId] = (name, cwd, pid);
            }
            catch
            {
                // Malformed or half-written registry entry — skip it.
            }
        }
        return result;
    }

    /// <summary>
    /// Full-file scan per session — unlike GetRecentTasks (which only reads
    /// a session's first ~30 lines), counting prompts needs every line.
    /// Only ever called from the periodic sampling timer (see
    /// TrayOrchestrator), never from a user-triggered path like /proyectos,
    /// since a long-running session's transcript can run into the hundreds
    /// of MB — fine on a 60-minute cadence, not fine on every command.
    /// </summary>
    public static Dictionary<string, int> GetPromptCountsByProject() => ScanPrompts().TotalsByProject;

    /// <summary>
    /// One full pass over every session file, gathering BOTH per-project
    /// totals and every real prompt's own embedded timestamp — the latter
    /// lets range-scoped counts (Hoy/Ayer/Semana/Mes) be computed by
    /// filtering timestamps already in memory instead of rescanning the
    /// transcripts from disk for every tab click, which is what made
    /// opening the Stats window or switching range tabs peg the CPU. See
    /// CodexProjectsHelper's identical method and PromptScanCache for the
    /// full reasoning and how this is meant to be consumed (cached,
    /// refreshed on a timer, never called directly from UI code).
    /// </summary>
    public static (Dictionary<string, int> TotalsByProject, List<DateTimeOffset> Timestamps) ScanPrompts()
    {
        var root = ProjectsRoot;
        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var timestamps = new List<DateTimeOffset>();
        if (!Directory.Exists(root)) return (totals, timestamps);

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            List<string> sessionFiles;
            try { sessionFiles = Directory.EnumerateFiles(dir, "*.jsonl").ToList(); }
            catch { continue; }
            if (sessionFiles.Count == 0) continue;

            var projectPath = TryReadCwd(sessionFiles[0]) ?? new DirectoryInfo(dir).Name;
            var total = 0;
            foreach (var file in sessionFiles)
            {
                var fileTimestamps = CountPromptTimestampsInFile(file);
                total += fileTimestamps.Count;
                timestamps.AddRange(fileTimestamps);
            }
            totals[projectPath] = total;
        }
        return (totals, timestamps);
    }

    /// <summary>
    /// Retries a couple of times on an IOException before giving up — a
    /// session file transiently locked by something else entirely (an
    /// antivirus scan, OneDrive/cloud sync, the Windows Search indexer)
    /// used to silently make this file contribute 0 prompts for that one
    /// scan; the NEXT successful scan would then see its full count appear
    /// out of nowhere and record it as a burst of brand-new prompts that
    /// were never actually typed. See CodexProjectsHelper's identical fix
    /// for the confirmed real-world repro.
    /// </summary>
    private static List<DateTimeOffset> CountPromptTimestampsInFile(string sessionFile)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return CountPromptTimestampsInFileCore(sessionFile);
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
            catch
            {
                // Malformed file, or still locked after retrying.
                return new List<DateTimeOffset>();
            }
        }
    }

    private static List<DateTimeOffset> CountPromptTimestampsInFileCore(string sessionFile)
    {
        var timestamps = new List<DateTimeOffset>();
        using var reader = new StreamReader(sessionFile);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (!string.IsNullOrEmpty(line) && TryGetRealUserPrompt(line, out var at)) timestamps.Add(at);
        }
        return timestamps;
    }

    /// <summary>
    /// A real user-typed prompt is a "type":"user" line that isn't a
    /// subagent/sidechain turn and isn't secretly a tool-result submission
    /// — Claude Code logs a tool's output being handed back as "type":"user"
    /// too, distinguishable by its "content" being an array of ONLY
    /// tool_result blocks rather than a plain string or a text block. Also
    /// captures the entry's own embedded "timestamp" field — see
    /// ScanPrompts's doc comment for why that's collected here.
    /// </summary>
    private static bool TryGetRealUserPrompt(string line, out DateTimeOffset at)
    {
        at = default;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "user") return false;
            if (root.TryGetProperty("isSidechain", out var sidechain) && sidechain.ValueKind == JsonValueKind.True) return false;
            if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content)) return false;

            var isReal = content.ValueKind switch
            {
                JsonValueKind.String => true,
                JsonValueKind.Array => content.EnumerateArray().Any(b => b.TryGetProperty("type", out var t) && t.GetString() == "text"),
                _ => false,
            };
            if (!isReal) return false;

            if (root.TryGetProperty("timestamp", out var tsProp) && tsProp.ValueKind == JsonValueKind.String)
            {
                DateTimeOffset.TryParse(tsProp.GetString(), out at);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadCwd(string sessionFile)
    {
        try
        {
            using var reader = new StreamReader(sessionFile);
            for (var i = 0; i < MaxLinesProbed && !reader.EndOfStream; i++)
            {
                var line = reader.ReadLine();
                if (string.IsNullOrEmpty(line) || line.Length > MaxProbeLineLength) continue;

                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("cwd", out var cwdProp) && cwdProp.ValueKind == JsonValueKind.String)
                {
                    return cwdProp.GetString();
                }
            }
        }
        catch
        {
            // Malformed line, file locked, etc. — the folder name is still a usable fallback.
        }
        return null;
    }
}
