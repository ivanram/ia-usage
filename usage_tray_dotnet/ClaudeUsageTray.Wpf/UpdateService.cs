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

        Log($"apply-update: waiting for pid {oldPid} to exit, target={targetPath}");
        WaitForProcessExit(oldPid, TimeSpan.FromSeconds(15));

        var selfPath = Environment.ProcessPath!;
        try
        {
            CopyWithRetry(selfPath, targetPath);
            Log("apply-update: copy done, relaunching target");
            Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"apply-update: failed: {ex}");
            // Best effort: if we couldn't replace it, at least leave the old build runnable.
            try { Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true }); } catch { /* nothing more we can do */ }
        }

        ScheduleSelfDelete(selfPath);
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

    /// <summary>
    /// Checks once, and if a newer release exists, asks the user. Silent
    /// startup checks (manualCheck: false) that find nothing newer just
    /// return — but a check the user explicitly asked for (clicking the
    /// version label in Settings) says so either way, since a silent
    /// no-op there would just look broken.
    /// </summary>
    public static async Task CheckAndPromptAsync(bool manualCheck = false)
    {
        try
        {
            var (version, downloadUrl) = await GetLatestReleaseAsync();
            if (version is null || downloadUrl is null)
            {
                if (manualCheck) MessageBox.Show("No se ha podido comprobar si hay actualizaciones.", "Uso de IA", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            Log($"current={current} latest={version}");
            if (!IsNewer(version, current))
            {
                if (manualCheck) MessageBox.Show("Ya tienes la última versión.", "Uso de IA", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                $"Hay una nueva versión disponible (v{version.Major}.{version.Minor}.{version.Build}).\n¿Quieres actualizarla ahora?",
                "Actualización disponible",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (result != MessageBoxResult.Yes) return;

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

    private static async Task<(Version? version, string? downloadUrl)> GetLatestReleaseAsync()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeUsageTray-UpdateChecker");
        using var resp = await http.GetAsync(RepoApiUrl);
        if (!resp.IsSuccessStatusCode)
        {
            Log($"GetLatestReleaseAsync: http {(int)resp.StatusCode}");
            return (null, null);
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var versionText = tag.TrimStart('v', 'V');
        if (!Version.TryParse(versionText, out var version)) return (null, null);

        string? downloadUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.StartsWith("ClaudeUsageTray", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
        }
        return (version, downloadUrl);
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
