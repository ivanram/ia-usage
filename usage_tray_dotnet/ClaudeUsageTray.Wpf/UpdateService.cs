using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace ClaudeUsageTray;

/// <summary>
/// Checks GitHub Releases for a newer build and, if the user agrees,
/// downloads it and swaps it in for the currently running exe. Settings
/// and the per-provider WebView2 login sessions all live under
/// %LocalAppData%\ClaudeUsageTray — completely separate from wherever the
/// exe itself sits — so replacing the exe file never touches them: no
/// re-login, no lost settings.
///
/// The swap is the classic "portable exe self-update" dance, since a
/// running exe can't overwrite its own file: download the new build to a
/// temp path, launch IT with --apply-update pointing back at our own
/// path+PID, and exit. That temp copy waits for us to actually exit,
/// copies itself over our original path, relaunches from there, then
/// deletes itself.
/// </summary>
internal static class UpdateService
{
    private const string RepoApiUrl = "https://api.github.com/repos/ivanram/ia-usage/releases/latest";
    private const string ApplyUpdateArg = "--apply-update";

    // Only ever appended to an --apply-update relaunch (see the
    // UnauthorizedAccessException branch below) — marks "this copy of the
    // updater is already running elevated", so it doesn't loop back into
    // asking for elevation again if something else goes wrong afterward.
    private const string ElevatedArg = "--elevated";

    private static readonly string DebugFile = Path.Combine(Paths.LogsDir, "update_debug.txt");
    private static void Log(string msg)
    {
        try { File.AppendAllText(DebugFile, $"{DateTime.Now:O} {msg}\n"); } catch { /* best effort */ }
    }

    /// <summary>
    /// Must be called at the very top of startup, before the single-
    /// instance mutex is taken — this process was launched BY the old
    /// version specifically to replace it while that one is still
    /// running, so it must never be blocked by that same mutex. Returns
    /// true if this launch was an update-apply step (caller should shut
    /// down immediately after) rather than a normal launch.
    /// </summary>
    public static bool TryHandleApplyUpdate(string[] args)
    {
        if (args.Length < 3 || args[0] != ApplyUpdateArg) return false;

        var targetPath = args[1];
        if (!int.TryParse(args[2], out var oldPid))
        {
            Log($"apply-update: bad pid arg '{args[2]}'");
            return true;
        }
        var alreadyElevated = args.Length > 3 && args[3] == ElevatedArg;

        Log($"apply-update: waiting for pid {oldPid} to exit, target={targetPath}");
        WaitForProcessExit(oldPid, TimeSpan.FromSeconds(15));

        var selfPath = Environment.ProcessPath!;
        try
        {
            CopyWithRetry(selfPath, targetPath);
            Log("apply-update: copy done, relaunching target");
            Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true });
            ScheduleSelfDelete(selfPath);
        }
        catch (UnauthorizedAccessException ex) when (!alreadyElevated)
        {
            // Someone installed (or the pre-2.0.1 installer put) the app
            // somewhere only an admin can write to, e.g. Program Files — a
            // plain user-rights File.Copy can't overwrite it. Relaunch this
            // same temp updater with a UAC prompt instead of just giving up;
            // if the user grants it, that elevated instance re-enters this
            // same method (with ElevatedArg set, so it can't loop back here
            // again) and finishes the copy with the rights it needs.
            Log($"apply-update: access denied, requesting elevation: {ex.Message}");
            try
            {
                Process.Start(new ProcessStartInfo(selfPath)
                {
                    Arguments = $"{ApplyUpdateArg} \"{targetPath}\" {oldPid} {ElevatedArg}",
                    UseShellExecute = true,
                    Verb = "runas",
                });
                // The elevated child owns selfPath and targetPath from here
                // (copy, relaunch, self-delete) — this instance must not
                // touch either, so it just steps aside.
            }
            catch (Exception elevateEx)
            {
                // UAC prompt was cancelled, or elevation itself failed.
                // Best effort: leave the old build runnable rather than
                // stranding the user with nothing open at all.
                Log($"apply-update: elevation request failed/cancelled: {elevateEx}");
                try { Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true }); } catch { /* nothing more we can do */ }
                ScheduleSelfDelete(selfPath);
            }
        }
        catch (Exception ex)
        {
            Log($"apply-update: failed: {ex}");
            // Best effort: if we couldn't replace it, at least leave the old build runnable.
            try { Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true }); } catch { /* nothing more we can do */ }
            ScheduleSelfDelete(selfPath);
        }

        return true;
    }

    private static void WaitForProcessExit(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try { Process.GetProcessById(pid); }
            catch (ArgumentException) { return; } // already gone
            Thread.Sleep(200);
        }
    }

    private static void CopyWithRetry(string sourcePath, string targetPath)
    {
        // The old process can take a moment to fully release its file lock
        // even after Process.GetProcessById says it's already gone.
        Exception? last = null;
        for (var i = 0; i < 20; i++)
        {
            try { File.Copy(sourcePath, targetPath, overwrite: true); return; }
            catch (IOException ex) { last = ex; Thread.Sleep(300); }
        }
        throw last ?? new IOException("No se pudo reemplazar el ejecutable");
    }

    private static void ScheduleSelfDelete(string selfPath)
    {
        // Can't delete our own exe file while we're still executing from
        // it. Hand cleanup to a detached shell command that waits a beat
        // after we've exited, then deletes the leftover temp download.
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/C timeout /T 3 >nul & del /F /Q \"{selfPath}\"")
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            Process.Start(psi);
        }
        catch { /* a leftover temp exe is harmless */ }
    }

    // "1 vez por hora como máximo" for the silent startup check — plenty
    // often enough to notice a new release without adding to GitHub's
    // unauthenticated rate limit. The manual button gets its own much
    // shorter cooldown purely as an anti-spam-click guard, not a real
    // throttle — a deliberate click is still answered within seconds.
    private static readonly TimeSpan AutoCheckMinInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan ManualCheckMinInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Checks once, and if a newer release exists, asks the user. Silent
    /// startup checks (manualCheck: false) that find nothing newer just
    /// return — but a check the user explicitly asked for (clicking the
    /// version label in Settings) says so either way, since a silent
    /// no-op there would just look broken. A silent check also respects
    /// the "buscar actualizaciones automáticamente" setting and any active
    /// "Hoy no, mañana" snooze — a manual check always ignores both, since
    /// clicking the version label is an explicit request no matter what.
    /// Both kinds are throttled against LastUpdateCheckAt (persisted, so it
    /// survives an app restart) so neither a busy startup/shutdown cycle
    /// nor mashing the version label can spam GitHub's API.
    /// </summary>
    public static async Task CheckAndPromptAsync(bool manualCheck = false)
    {
        try
        {
            var settings = AppSettings.Load();
            if (!manualCheck)
            {
                if (!settings.AutoCheckUpdates) return;
                if (settings.UpdateSnoozeUntil is { } snoozeUntil && DateTime.Now < snoozeUntil) return;
            }

            var minInterval = manualCheck ? ManualCheckMinInterval : AutoCheckMinInterval;
            if (settings.LastUpdateCheckAt is { } lastCheck && DateTime.Now - lastCheck < minInterval)
            {
                Log($"CheckAndPromptAsync: throttled, {(DateTime.Now - lastCheck).TotalSeconds:0}s since last check (min {minInterval.TotalSeconds:0}s)");
                if (manualCheck) AppDialogWindow.ShowInfo("", Strings.T("dialog.toosoon.message"));
                return;
            }

            // Recorded before the actual request — a failed/rate-limited
            // attempt still counts as "just checked" so a burst of retries
            // during an outage doesn't itself become the thing exhausting
            // the rate limit.
            settings.LastUpdateCheckAt = DateTime.Now;
            settings.Save();

            var (version, downloadUrl, changelog, rateLimited) = await GetLatestReleaseAsync();
            if (version is null || downloadUrl is null)
            {
                if (manualCheck)
                {
                    var messageKey = rateLimited ? "dialog.ratelimited.message" : "dialog.checkfailed.message";
                    AppDialogWindow.ShowInfo("", Strings.T(messageKey));
                }
                return;
            }

            var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            Log($"current={current} latest={version}");
            if (!IsNewer(version, current))
            {
                if (manualCheck) AppDialogWindow.ShowInfo("", Strings.T("dialog.uptodate.message"));
                return;
            }

            var choice = AppDialogWindow.ShowUpdatePrompt(
                Strings.T("dialog.update.title"),
                Strings.F("dialog.update.message", $"{version.Major}.{version.Minor}.{version.Build}"),
                changelog);

            if (choice == DialogChoice.Later)
            {
                settings.UpdateSnoozeUntil = DateTime.Today.AddDays(1);
                settings.Save();
                return;
            }
            if (choice != DialogChoice.Yes) return;

            await DownloadAndApplyAsync(downloadUrl, version);
        }
        catch (Exception ex)
        {
            Log($"CheckAndPromptAsync failed: {ex}");
        }
    }

    /// <summary>
    /// Compares only Major.Minor.Build — GitHub tags and our csproj
    /// &lt;Version&gt; are always 3-part, but the assembly version .NET
    /// derives from it is 4-part with Revision=0, and a bare
    /// System.Version comparison treats an *unset* (-1) Revision as less
    /// than 0 — which would make "latest 1.1.2" look newer than
    /// "current 1.1.2.0" forever and loop the update prompt.
    /// </summary>
    private static bool IsNewer(Version latest, Version current)
    {
        if (latest.Major != current.Major) return latest.Major > current.Major;
        if (latest.Minor != current.Minor) return latest.Minor > current.Minor;
        return latest.Build > current.Build;
    }

    /// <summary>
    /// GitHub's REST API caps unauthenticated requests at 60/hour per IP —
    /// easy to hit during active development (repeated manual checks +
    /// every build's startup check, all from the same machine) but very
    /// unlikely in normal single-user usage (one silent check at startup
    /// a day, plus the occasional manual click). Surfaced separately from
    /// other failures so the dialog can say "try again shortly" instead of
    /// a generic, slightly alarming "couldn't check for updates".
    /// </summary>
    private static async Task<(Version? version, string? downloadUrl, string? changelog, bool rateLimited)> GetLatestReleaseAsync()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeUsageTray-UpdateChecker");
        using var resp = await http.GetAsync(RepoApiUrl);
        if (!resp.IsSuccessStatusCode)
        {
            Log($"GetLatestReleaseAsync: http {(int)resp.StatusCode}");
            return (null, null, null, resp.StatusCode == System.Net.HttpStatusCode.Forbidden);
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var versionText = tag.TrimStart('v', 'V');
        if (!Version.TryParse(versionText, out var version)) return (null, null, null, false);

        // The release body is the exact changelog written when the release
        // was published (see the "Add changelog generation to the release
        // process" note) — shown as-is in the update prompt so the user
        // sees what they're about to install, straight from the source of
        // truth instead of a separate copy that could drift out of sync.
        var changelog = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null;

        // Each release carries two assets: the framework-dependent "-fx"
        // build (a few MB, needs the .NET 8 Desktop Runtime already on the
        // machine — which this same running process is living proof of)
        // and the older self-contained one (much bigger, no dependency) for
        // machines without that runtime. Since this code only runs on a
        // machine already running a .NET app, the "-fx" asset is always the
        // right pick when present; the self-contained one is kept only as a
        // fallback for releases published before this asset existed.
        string? fxUrl = null;
        string? fallbackUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith("ClaudeUsageTray", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                var url = asset.GetProperty("browser_download_url").GetString();
                if (name.EndsWith("-fx.exe", StringComparison.OrdinalIgnoreCase)) fxUrl ??= url;
                else fallbackUrl ??= url;
            }
        }
        return (version, fxUrl ?? fallbackUrl, changelog, false);
    }

    private static async Task DownloadAndApplyAsync(string downloadUrl, Version version)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ClaudeUsageTray-{version}-update.exe");
        Log($"Downloading {downloadUrl} -> {tempPath}");

        using (var http = new HttpClient())
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeUsageTray-UpdateChecker");
            using var resp = await http.GetAsync(downloadUrl);
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(tempPath);
            await resp.Content.CopyToAsync(fs);
        }

        var currentExePath = Environment.ProcessPath!;
        var currentPid = Environment.ProcessId;
        Log($"Launching updater: {tempPath} {ApplyUpdateArg} \"{currentExePath}\" {currentPid}");

        Process.Start(new ProcessStartInfo(tempPath)
        {
            Arguments = $"{ApplyUpdateArg} \"{currentExePath}\" {currentPid}",
            UseShellExecute = true,
        });

        // Let the new process take over from here; this instance exits cleanly.
        Application.Current.Shutdown();
    }
}
