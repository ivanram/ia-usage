using System.IO;
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
    public bool ShowGrok { get; set; } = false;
    public PopupMode PopupMode { get; set; } = PopupMode.Rich;
    /// <summary>Seconds the cursor must rest on the tray icon before the panel opens. 0 = instant.</summary>
    public int HoverDelaySeconds { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.System;
    /// <summary>Either <see cref="OriginalAccentSentinel"/> (percent-based bar colors) or a literal "#RRGGBB".</summary>
    public string AccentColor { get; set; } = OriginalAccentSentinel;
    public bool AnimationsEnabled { get; set; } = true;
    public bool TelegramEnabled { get; set; } = true;
    public string? TelegramBotToken { get; set; }
    public long? TelegramChatId { get; set; }

    public const string OriginalAccentSentinel = "ORIGINAL";

    public bool NotifyResetClaude { get; set; }
    public bool NotifyResetChatGpt { get; set; }
    public bool NotifyResetGrok { get; set; }
    public bool NotifySoundEnabled { get; set; }

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
