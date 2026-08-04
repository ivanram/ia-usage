using System.IO;

namespace ClaudeUsageTray;

/// <summary>
/// Manages a shortcut in the current user's Startup folder. The shortcut's
/// mere existence is the source of truth (no separate flag to fall out of
/// sync with) — checked/created/removed directly on disk.
/// </summary>
internal static class AutoStartHelper
{
    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "ClaudeUsageTray.lnk");

    public static bool IsEnabled() => File.Exists(ShortcutPath);

    public static void SetEnabled(bool enabled)
    {
        if (enabled) Create();
        else if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
    }

    /// <summary>
    /// Call once per app startup. The shortcut's target is whatever exe
    /// path was current the moment it was last written — this app has no
    /// stable install location (each release/rebuild is its own
    /// version-numbered exe), so a shortcut written against an older
    /// version silently stops working the moment that file is gone: no
    /// error at logon, it just doesn't launch. Re-pointing it at whatever
    /// exe is running right now, every launch, makes "iniciar con Windows"
    /// self-heal instead of quietly going stale after the next update.
    /// </summary>
    public static void SyncIfEnabled()
    {
        if (!IsEnabled()) return;
        if (TargetMatches(Environment.ProcessPath!)) return;
        Create();
    }

    private static bool TargetMatches(string exePath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return false;
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(ShortcutPath);
                string target = shortcut.TargetPath;
                return string.Equals(target, exePath, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
        }
        catch
        {
            return false;
        }
    }

    private static void Create()
    {
        var exePath = Environment.ProcessPath!;
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(ShortcutPath);
            shortcut.TargetPath = exePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
            shortcut.Description = "Claude/ChatGPT Usage Tray";
            shortcut.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }
}
