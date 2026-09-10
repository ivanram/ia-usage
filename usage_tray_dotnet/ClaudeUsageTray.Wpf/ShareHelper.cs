using System.Diagnostics;

namespace ClaudeUsageTray;

/// <summary>
/// Opens Windows' native Share flyout (the same one Explorer's right-click
/// "Compartir" shows) for a single file, via the "share" shell verb Windows
/// 10+ registers for ordinary files — this avoids the much heavier route of
/// hosting the WinRT DataTransferManager contract from a classic WPF app
/// (custom COM interop, a Windows-versioned TargetFramework, manually
/// answering DataRequested...) for what is, from here, a one-line ask.
/// </summary>
internal static class ShareHelper
{
    public static bool TryShareFile(string filePath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                Verb = "share",
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Fallback when the "share" verb isn't available (older Windows, or no shell handler for this file) — reveals the file in Explorer so the user can share it by hand instead.</summary>
    public static void RevealInExplorer(string filePath)
    {
        try { Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{filePath}\"", UseShellExecute = true }); }
        catch { /* best effort */ }
    }
}
