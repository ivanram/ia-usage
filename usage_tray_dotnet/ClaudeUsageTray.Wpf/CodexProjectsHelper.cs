using System.IO;
using System.Text.Json;
using System.Threading;

namespace ClaudeUsageTray;

/// <summary>
/// Reads OpenAI Codex's own local session history (%USERPROFILE%\.codex\)
/// for the /proyectos Telegram command, the same read-only-metadata
/// approach as ClaudeCodeProjectsHelper. Codex lays sessions out
/// differently than Claude Code though: rollout files live under
/// sessions\YYYY\MM\DD\ (not grouped by project folder), and a single
/// thread (session_id) can span several rollout files across resumes — so
/// this aggregates by session_id into one task per thread, using the most
/// recent rollout file's write time as that thread's activity. There's no
/// per-instance "still running" registry like Claude Code has, so a write
/// within the last couple of minutes stands in as the "active now" signal.
/// Thread display names come from a separate append-only
/// session_index.jsonl (id + thread_name + updated_at — a thread can be
/// renamed, so the LAST entry per id wins). The project's own display name
/// (what shows in Codex Desktop's sidebar, e.g. "Fuentes Madrid" for a
/// folder actually called "fuentes-android") is a THIRD, separate piece of
/// state — see LoadProjectNamesByRoot.
/// </summary>
internal static class CodexProjectsHelper
{
    private const string AgentName = "Codex";

    // Codex's own rollout files are small at the top (session_meta is
    // always line 0) — reading just that one line per file keeps this fast
    // even across dozens of historical sessions.
    private const int MaxFilesScanned = 300;
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(2);

    private static string CodexRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    private static string SessionsRoot => Path.Combine(CodexRoot, "sessions");
    private static string SessionIndexFile => Path.Combine(CodexRoot, "session_index.jsonl");
    private static string GlobalStateFile => Path.Combine(CodexRoot, ".codex-global-state.json");

    /// <summary>One entry per thread (session_id), not per project — the caller groups by ProjectPath itself.</summary>
    public static List<AgentTask> GetRecentTasks(int maxTasks = 100)
    {
        if (!Directory.Exists(SessionsRoot)) return new List<AgentTask>();

        var threadNames = LoadThreadNames();
        var projectNamesByRoot = LoadProjectNamesByRoot();

        List<string> files;
        try { files = Directory.EnumerateFiles(SessionsRoot, "*.jsonl", SearchOption.AllDirectories).ToList(); }
        catch { return new List<AgentTask>(); }

        // Only the most recently touched files matter for "what am I
        // working on" — capped so a huge multi-year history can't turn
        // this into a slow full scan.
        var recent = files
            .Select(f => (Path: f, Mtime: TryGetMtime(f)))
            .OrderByDescending(f => f.Mtime)
            .Take(MaxFilesScanned);

        // A thread can be resumed into multiple rollout files — keep only
        // the most recent one per session_id.
        var bySessionId = new Dictionary<string, (string Cwd, DateTime LastActivity)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, mtime) in recent)
        {
            var meta = TryReadSessionMeta(path);
            if (meta is not { Cwd.Length: > 0, SessionId.Length: > 0 }) continue;

            if (!bySessionId.TryGetValue(meta.Value.SessionId!, out var existing) || mtime > existing.LastActivity)
            {
                bySessionId[meta.Value.SessionId!] = (meta.Value.Cwd!, mtime);
            }
        }

        var result = bySessionId.Select(kv =>
        {
            var (sessionId, (cwd, lastActivity)) = (kv.Key, kv.Value);
            var isActive = DateTime.Now - lastActivity < ActiveWindow;
            var name = threadNames.TryGetValue(sessionId, out var n) ? n : null;
            // A Codex "project" can span several root folders (e.g. a repo's
            // checkout plus its build output dir) that all share one
            // display name — grouping by that name instead of the raw cwd
            // is what keeps those from splitting into separate project
            // entries in /proyectos.
            var project = projectNamesByRoot.TryGetValue(cwd.TrimEnd('\\', '/'), out var projectName) ? projectName : cwd;
            return new AgentTask(AgentName, project, lastActivity, isActive, name);
        });

        return result.OrderByDescending(t => t.LastActivity).Take(maxTasks).ToList();
    }

    /// <summary>
    /// Full-file scan per rollout — unlike GetRecentTasks (which only reads
    /// a file's first line), counting prompts needs every line. Only ever
    /// called from the periodic sampling timer (see TrayOrchestrator),
    /// never a user-triggered path — deliberately NOT capped by
    /// MaxFilesScanned like GetRecentTasks either, since undercounting here
    /// would silently make every later delta wrong, not just show a
    /// slightly-stale list.
    /// </summary>
    public static Dictionary<string, int> GetPromptCountsByProject()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(SessionsRoot)) return result;

        var projectNamesByRoot = LoadProjectNamesByRoot();

        List<string> files;
        try { files = Directory.EnumerateFiles(SessionsRoot, "*.jsonl", SearchOption.AllDirectories).ToList(); }
        catch { return result; }

        foreach (var file in files)
        {
            var (cwd, promptCount) = ReadCwdAndCountPrompts(file);
            if (cwd is not { Length: > 0 } || promptCount == 0) continue;

            var project = projectNamesByRoot.TryGetValue(cwd.TrimEnd('\\', '/'), out var name) ? name : cwd;
            result[project] = result.TryGetValue(project, out var existing) ? existing + promptCount : promptCount;
        }
        return result;
    }

    /// <summary>
    /// Total real user prompts with their OWN embedded timestamp inside
    /// [since, until) — computed by scanning the transcripts fresh right
    /// now, not by diffing periodic external snapshots the way the "Nuevos"
    /// dashboard/chart values used to. That diffing approach assumed every
    /// scan sees a project's true total, so any one bad read (a file
    /// transiently or even persistently locked by something else — an
    /// antivirus scan, cloud sync, the Windows Search indexer) got recorded
    /// as fact, and the next GOOD read then looked like a burst of
    /// brand-new prompts that were never actually typed. A prompt's own
    /// timestamp doesn't care when, or how reliably, we happened to be
    /// watching — confirmed against two separate real incidents (a
    /// dip-then-recover and a silent undercount that self-corrected after
    /// an overnight gap) that a snapshot-diff fix could paper over but
    /// never fully prevent.
    /// </summary>
    public static int GetPromptCountInRange(DateTimeOffset since, DateTimeOffset? until)
    {
        if (!Directory.Exists(SessionsRoot)) return 0;

        List<string> files;
        try { files = Directory.EnumerateFiles(SessionsRoot, "*.jsonl", SearchOption.AllDirectories).ToList(); }
        catch { return 0; }

        var total = 0;
        foreach (var file in files)
        {
            total += CountPromptsInRange(file, since, until);
        }
        return total;
    }

    private static int CountPromptsInRange(string path, DateTimeOffset since, DateTimeOffset? until)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return CountPromptsInRangeCore(path, since, until);
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
            catch
            {
                return 0;
            }
        }
    }

    private static int CountPromptsInRangeCore(string path, DateTimeOffset since, DateTimeOffset? until)
    {
        var count = 0;
        using var reader = new StreamReader(path);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrEmpty(line)) continue;
            if (!TryGetRealUserPrompt(line, out var at)) continue;
            if (at < since || (until.HasValue && at >= until.Value)) continue;
            count++;
        }
        return count;
    }

    /// <summary>
    /// One pass over the file covers both the session_meta line 0 (for cwd)
    /// and every line's prompt check, instead of opening the file twice.
    /// Retries a couple of times on an IOException before giving up — a
    /// rollout file transiently locked by something else entirely (an
    /// antivirus scan, OneDrive/cloud sync, the Windows Search indexer)
    /// used to silently make this file contribute 0 prompts for that one
    /// scan; the NEXT successful scan would then see its full count appear
    /// out of nowhere and record it as a burst of brand-new prompts that
    /// were never actually typed (confirmed against a real history.db: one
    /// project's total jumped by 10 between two samples with zero rollout
    /// files touched on disk in between).
    /// </summary>
    private static (string? Cwd, int PromptCount) ReadCwdAndCountPrompts(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return ReadCwdAndCountPromptsCore(path);
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
            catch
            {
                // Malformed file, or still locked after retrying — return nothing gathered.
                return (null, 0);
            }
        }
    }

    private static (string? Cwd, int PromptCount) ReadCwdAndCountPromptsCore(string path)
    {
        string? cwd = null;
        var count = 0;
        using var reader = new StreamReader(path);
        var isFirstLine = true;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrEmpty(line)) continue;
            if (isFirstLine)
            {
                cwd = TryExtractSessionMetaCwd(line);
                isFirstLine = false;
            }
            if (IsRealUserPrompt(line)) count++;
        }
        return (cwd, count);
    }

    private static string? TryExtractSessionMetaCwd(string firstLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(firstLine);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "session_meta") return null;
            if (!root.TryGetProperty("payload", out var payload)) return null;
            return payload.TryGetProperty("cwd", out var cwdProp) && cwdProp.ValueKind == JsonValueKind.String ? cwdProp.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A real user-typed prompt is a response_item message with role
    /// "user" whose text doesn't start with "&lt;" — Codex also injects
    /// synthetic user-role turns (environment context, permission
    /// instructions, etc.) that are never something a human actually
    /// typed; those all show up as XML-ish tag-wrapped text, which real
    /// prompts essentially never start with.
    /// </summary>
    private static bool IsRealUserPrompt(string line) => TryGetRealUserPrompt(line, out _);

    /// <summary>
    /// Same detection as before, plus the entry's own embedded
    /// "timestamp" field — used by GetPromptCountInRange to filter by
    /// calendar range directly from the data itself, rather than diffing
    /// periodic external snapshots (see that method's own doc comment for
    /// why that distinction matters).
    /// </summary>
    private static bool TryGetRealUserPrompt(string line, out DateTimeOffset at)
    {
        at = default;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "response_item") return false;
            if (!root.TryGetProperty("payload", out var payload)) return false;
            if (!payload.TryGetProperty("type", out var payloadType) || payloadType.GetString() != "message") return false;
            if (!payload.TryGetProperty("role", out var roleProp) || roleProp.GetString() != "user") return false;
            if (!payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return false;

            var hasRealPrompt = false;
            foreach (var block in content.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var blockType) || blockType.GetString() != "input_text") continue;
                if (!block.TryGetProperty("text", out var textProp) || textProp.ValueKind != JsonValueKind.String) continue;
                var text = textProp.GetString();
                if (!string.IsNullOrWhiteSpace(text) && !text.TrimStart().StartsWith('<')) { hasRealPrompt = true; break; }
            }
            if (!hasRealPrompt) return false;

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

    private static DateTime TryGetMtime(string path)
    {
        try { return File.GetLastWriteTime(path); } catch { return DateTime.MinValue; }
    }

    private static (string? Cwd, string? SessionId)? TryReadSessionMeta(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            var line = reader.ReadLine();
            if (string.IsNullOrEmpty(line)) return null;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "session_meta") return null;
            if (!root.TryGetProperty("payload", out var payload)) return null;

            var cwd = payload.TryGetProperty("cwd", out var cwdProp) && cwdProp.ValueKind == JsonValueKind.String ? cwdProp.GetString() : null;
            var sessionId = payload.TryGetProperty("session_id", out var sidProp) && sidProp.ValueKind == JsonValueKind.String ? sidProp.GetString() : null;
            return (cwd, sessionId);
        }
        catch
        {
            // Malformed/half-written rollout file — skip it.
            return null;
        }
    }

    /// <summary>
    /// Codex Desktop's own global state file keeps a "local-projects" map —
    /// each entry has an id, a user-facing name, and one or more rootPaths.
    /// This is the ONLY place that custom name lives; nothing in the
    /// rollout files or session index carries it, which is why relying on
    /// Path.GetFileName(cwd) alone showed the raw folder name instead
    /// (e.g. "fuentes-android") rather than what Codex Desktop itself
    /// displays for that project (e.g. "Fuentes Madrid").
    /// </summary>
    private static Dictionary<string, string> LoadProjectNamesByRoot()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(GlobalStateFile)) return result;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(GlobalStateFile));
            if (!doc.RootElement.TryGetProperty("local-projects", out var projects) || projects.ValueKind != JsonValueKind.Object) return result;

            foreach (var project in projects.EnumerateObject())
            {
                if (project.Value.ValueKind != JsonValueKind.Object) continue;
                if (!project.Value.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String) continue;
                if (nameProp.GetString() is not { Length: > 0 } name) continue;
                if (!project.Value.TryGetProperty("rootPaths", out var roots) || roots.ValueKind != JsonValueKind.Array) continue;

                foreach (var root in roots.EnumerateArray())
                {
                    if (root.ValueKind == JsonValueKind.String && root.GetString() is { Length: > 0 } rootPath)
                    {
                        result[rootPath.TrimEnd('\\', '/')] = name;
                    }
                }
            }
        }
        catch
        {
            // Best effort — missing/changed schema just falls back to raw folder names.
        }
        return result;
    }

    private static Dictionary<string, string> LoadThreadNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(SessionIndexFile)) return result;

        try
        {
            foreach (var line in File.ReadLines(SessionIndexFile))
            {
                if (string.IsNullOrEmpty(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                        && root.TryGetProperty("thread_name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                    {
                        result[idProp.GetString()!] = nameProp.GetString()!;
                    }
                }
                catch
                {
                    // One bad line shouldn't lose the rest of the index.
                }
            }
        }
        catch
        {
            // Best effort — missing names just fall back to the folder name.
        }
        return result;
    }
}
