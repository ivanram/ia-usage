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
