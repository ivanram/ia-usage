using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace ClaudeUsageTray;

internal static class ThemeHelper
{
    /// <summary>The literal color used for the app's UI accent (buttons, switches) when "Original" is selected.</summary>
    public const string OriginalSwatchColor = "#2E4372";

    /// <summary>
    /// The navy above reads as near-invisible on a dark background — same
    /// hue family, but lighter and more saturated so it still pops once
    /// the base theme flips to dark.
    /// </summary>
    public const string OriginalSwatchColorDark = "#7C97E0";

    public static readonly string[] AccentSwatches =
    {
        "#378ADD", "#7F77DD", "#1D9E75", "#D85A30", "#D4537E", "#639922",
        "#D64545", "#B37D0F", "#5B6472", "#127F9E", "#5457C9",
    };

    /// <summary>Applies the theme app-wide immediately, so both startup and the live preview in Settings share one code path.</summary>
    public static void Apply(AppTheme theme)
    {
        var isDark = ResolveIsDark(theme);
        var paletteHelper = new PaletteHelper();
        var palette = paletteHelper.GetTheme();
        palette.SetBaseTheme(isDark ? BaseTheme.Dark : BaseTheme.Light);
        paletteHelper.SetTheme(palette);
    }

    /// <summary>
    /// Applies the accent color app-wide (drives MaterialDesign.Brush.Primary
    /// and friends everywhere). Relies on Apply(theme) having already run
    /// for this call — every call site does that in the same breath — so
    /// the base theme it reads back here is current.
    /// </summary>
    public static void ApplyAccent(string accentSetting)
    {
        var paletteHelper = new PaletteHelper();
        var palette = paletteHelper.GetTheme();
        var isDark = palette.GetBaseTheme() == BaseTheme.Dark;

        var hex = accentSetting == AppSettings.OriginalAccentSentinel
            ? (isDark ? OriginalSwatchColorDark : OriginalSwatchColor)
            : accentSetting;
        var color = (Color)ColorConverter.ConvertFromString(hex);
        palette.SetPrimaryColor(color);
        paletteHelper.SetTheme(palette);
    }

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

    public static bool ResolveIsDark(AppTheme theme) => theme switch
    {
        AppTheme.Light => false,
        AppTheme.Dark => true,
        _ => IsSystemDarkMode(),
    };
}
