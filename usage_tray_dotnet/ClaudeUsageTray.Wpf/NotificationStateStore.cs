using System.IO;
using System.Text.Json;

namespace ClaudeUsageTray;

/// <summary>
/// Persists, per service+bar, which usage-threshold "episode" (none / 80% /
/// exhausted) Telegram has already been told about. TrayOrchestrator's own
/// in-memory _lastPercents starts empty every launch, so without this a
/// service still sitting at 100% would get re-announced by Telegram on
/// every single app restart — fine for the desktop toast (a subtle,
/// once-per-launch reminder), genuinely annoying for a phone push. An
/// episode only clears once the bar actually drops back under 80% (a real
/// reset), so the next real crossing notifies again as normal.
/// </summary>
public sealed class NotificationStateStore
{
    public Dictionary<string, string> Level { get; set; } = new();

    private static string FilePath => Path.Combine(Paths.AppDataDir, "notification_state.json");

    public static NotificationStateStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<NotificationStateStore>(json);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // Corrupt file — start fresh rather than crash the refresh cycle over it.
        }
        return new NotificationStateStore();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var tmpPath = FilePath + ".tmp";
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmpPath, FilePath, overwrite: true);
        }
        catch
        {
            // Best effort — a missed save just risks one extra Telegram ping later, not a crash.
        }
    }
}
