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
    /// of MB — fine on a 30-minute cadence, not fine on every command.
    /// </summary>
    public static Dictionary<string, int> GetPromptCountsByProject()
    {
        var root = ProjectsRoot;
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return result;

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
                total += CountPromptsInFile(file);
            }
            result[projectPath] = total;
        }
        return result;
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
    private static int CountPromptsInFile(string sessionFile)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return CountPromptsInFileCore(sessionFile);
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
            catch
            {
                // Malformed file, or still locked after retrying.
                return 0;
            }
        }
    }

    private static int CountPromptsInFileCore(string sessionFile)
    {
        var count = 0;
        using var reader = new StreamReader(sessionFile);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (!string.IsNullOrEmpty(line) && IsRealUserPrompt(line)) count++;
        }
        return count;
    }

    /// <summary>
    /// A real user-typed prompt is a "type":"user" line that isn't a
    /// subagent/sidechain turn and isn't secretly a tool-result submission
    /// — Claude Code logs a tool's output being handed back as "type":"user"
    /// too, distinguishable by its "content" being an array of ONLY
    /// tool_result blocks rather than a plain string or a text block.
    /// </summary>
    private static bool IsRealUserPrompt(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "user") return false;
            if (root.TryGetProperty("isSidechain", out var sidechain) && sidechain.ValueKind == JsonValueKind.True) return false;
            if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content)) return false;

            return content.ValueKind switch
            {
                JsonValueKind.String => true,
                JsonValueKind.Array => content.EnumerateArray().Any(b => b.TryGetProperty("type", out var t) && t.GetString() == "text"),
                _ => false,
            };
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
