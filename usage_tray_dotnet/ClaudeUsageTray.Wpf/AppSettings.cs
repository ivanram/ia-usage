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

public enum PopupWindowStyle
{
    Standard,
    Blur,
}

public sealed class AppSettings
{
    public int RefreshMinutes { get; set; } = 10;
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
    public PopupWindowStyle PopupWindowStyleMode { get; set; } = PopupWindowStyle.Standard;
    /// <summary>1-100. Standard mode only: alpha of the popup's background fill — the text/bars on top stay fully opaque regardless.</summary>
    public int PopupOpacityPercent { get; set; } = 100;
    /// <summary>1-100. Blur mode only: how strong the frosted-glass effect looks — see PopupWindow.ApplyThemeColors for why this drives a tint alpha rather than an actual blur radius (Windows doesn't expose one).</summary>
    public int PopupBlurPercent { get; set; } = 45;
    public bool TelegramEnabled { get; set; } = true;
    public string? TelegramBotToken { get; set; }
    public long? TelegramChatId { get; set; }

    /// <summary>Whether the popup is currently showing its compact layout — toggled from the popup itself, not the Settings window, and persisted so a pinned compact panel reopens compact next launch.</summary>
    public bool PopupCompactMode { get; set; }
    /// <summary>Which services appear in the compact layout — independent of ShowClaude/ShowChatGpt/ShowGrok, which control the full layout instead.</summary>
    public bool CompactShowClaude { get; set; } = true;
    public bool CompactShowChatGpt { get; set; } = false;
    public bool CompactShowGrok { get; set; } = false;

    public const string OriginalAccentSentinel = "ORIGINAL";

    public bool NotifyResetClaude { get; set; } = true;
    public bool NotifyResetChatGpt { get; set; } = true;
    public bool NotifyResetGrok { get; set; } = true;
    public bool NotifySoundEnabled { get; set; }

    /// <summary>Forwards the same reset/exhausted text the desktop toast shows to the bound Telegram chat.</summary>
    public bool TelegramNotifyUsage { get; set; }
    /// <summary>Only meaningful alongside <see cref="TelegramNotifyUsage"/> — an extra Telegram-only heads-up the first time a service's primary bar crosses 80%.</summary>
    public bool TelegramNotify80Percent { get; set; }

    public AppLanguage Language { get; set; } = AppLanguage.Spanish;
    public bool AutoCheckUpdates { get; set; } = true;
    /// <summary>Set by the "Hoy no, mañana" button on the update dialog — silent startup checks skip until this passes; a manual check always ignores it.</summary>
    public DateTime? UpdateSnoozeUntil { get; set; }
    /// <summary>When the app last actually queried GitHub for a release, successful or not — see UpdateService's throttling.</summary>
    public DateTime? LastUpdateCheckAt { get; set; }

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
