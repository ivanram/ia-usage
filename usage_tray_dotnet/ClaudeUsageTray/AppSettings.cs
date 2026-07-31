using System.Text.Json;

namespace ClaudeUsageTray;

public enum PopupMode
{
    Rich,
    Tooltip,
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed class AppSettings
{
    public int RefreshMinutes { get; set; } = 5;
    public bool ShowClaude { get; set; } = true;
    public bool ShowChatGpt { get; set; } = false;
    public PopupMode PopupMode { get; set; } = PopupMode.Rich;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool TelegramEnabled { get; set; } = true;
    public string? TelegramBotToken { get; set; }
    public long? TelegramChatId { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeUsageTray", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // Ignore corrupt settings file, fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        // Write-to-temp-then-replace so a crash or force-kill mid-write can
        // never leave settings.json truncated or corrupt.
        var tmpPath = FilePath + ".tmp";
        File.WriteAllText(tmpPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmpPath, FilePath, overwrite: true);
    }
}
