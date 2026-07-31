using System.IO;

namespace ClaudeUsageTray;

/// <summary>
/// Central place for every writable path the app uses, all rooted under
/// %LOCALAPPDATA%\ClaudeUsageTray — never next to the exe. Log/status files
/// used to be written beside the running executable, which meant they
/// landed wherever the user happened to put the portable exe (a friend's
/// Desktop, in one case) instead of a proper per-user app data folder.
/// </summary>
internal static class Paths
{
    public static string AppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageTray");

    public static string LogsDir
    {
        get
        {
            var dir = Path.Combine(AppDataDir, "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
