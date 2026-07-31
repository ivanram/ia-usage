namespace ClaudeUsageTray;

internal static class ThemeHelper
{
    public static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v == 0;
        }
        catch
        {
            // Fall through to default.
        }
        return false;
    }
}

public sealed class ThemePalette
{
    public required Color RingBg { get; init; }
    public required Color CardBg { get; init; }
    public required Color Border { get; init; }
    public required Color TitleText { get; init; }
    public required Color BodyText { get; init; }
    public required Color MutedText { get; init; }
    public required Color TrackBg { get; init; }
    public required Color LinkText { get; init; }
    public required Color LinkHoverText { get; init; }
    public required Color ErrorText { get; init; }
    public required Color SeparatorColor { get; init; }

    public static bool ResolveIsDark(AppTheme theme) => theme switch
    {
        AppTheme.Light => false,
        AppTheme.Dark => true,
        _ => ThemeHelper.IsSystemDarkMode(),
    };

    public static ThemePalette Resolve(AppTheme theme) => ResolveIsDark(theme) ? Dark : Light;

    public static readonly ThemePalette Dark = new()
    {
        RingBg = Color.FromArgb(255, 58, 58, 62),
        CardBg = Color.FromArgb(255, 30, 30, 33),
        Border = Color.FromArgb(255, 58, 58, 62),
        TitleText = Color.White,
        BodyText = Color.FromArgb(255, 224, 224, 228),
        MutedText = Color.FromArgb(255, 152, 152, 158),
        TrackBg = Color.FromArgb(255, 60, 60, 65),
        LinkText = Color.FromArgb(255, 129, 180, 255),
        LinkHoverText = Color.FromArgb(255, 172, 205, 255),
        ErrorText = Color.FromArgb(255, 240, 132, 132),
        SeparatorColor = Color.FromArgb(255, 55, 55, 60),
    };

    public static readonly ThemePalette Light = new()
    {
        RingBg = Color.FromArgb(255, 218, 218, 222),
        CardBg = Color.FromArgb(255, 250, 250, 252),
        Border = Color.FromArgb(255, 218, 218, 222),
        TitleText = Color.FromArgb(255, 28, 28, 30),
        BodyText = Color.FromArgb(255, 45, 45, 48),
        MutedText = Color.FromArgb(255, 114, 114, 120),
        TrackBg = Color.FromArgb(255, 227, 227, 231),
        LinkText = Color.FromArgb(255, 0, 95, 184),
        LinkHoverText = Color.FromArgb(255, 0, 70, 140),
        ErrorText = Color.FromArgb(255, 196, 43, 43),
        SeparatorColor = Color.FromArgb(255, 228, 228, 232),
    };
}
