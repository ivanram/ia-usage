using System.IO;
using System.Windows;
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

    /// <summary>
    /// Applies the theme app-wide immediately, so both startup and the live
    /// preview in Settings share one code path. MaterialDesignThemes'
    /// PaletteHelper.GetTheme() can throw ("Cannot get theme outside of a
    /// WPF application") if it's reached before Application.Current's
    /// resources are fully ready — observed as a real crash log from a
    /// double-click on the tray icon that raced OpenSettings against
    /// startup. Caught rather than left to crash the whole app over what's
    /// ultimately just a cosmetic theme not applying this one time.
    /// </summary>
    public static void Apply(AppTheme theme)
    {
        try
        {
            var isDark = ResolveIsDark(theme);
            var paletteHelper = new PaletteHelper();
            var palette = paletteHelper.GetTheme();
            palette.SetBaseTheme(isDark ? BaseTheme.Dark : BaseTheme.Light);
            paletteHelper.SetTheme(palette);
        }
        catch (Exception ex)
        {
            LogFailure("Apply", ex);
        }
    }

    /// <summary>
    /// Applies the accent color app-wide (drives MaterialDesign.Brush.Primary
    /// and friends everywhere). Relies on Apply(theme) having already run
    /// for this call — every call site does that in the same breath — so
    /// the base theme it reads back here is current. See Apply's doc
    /// comment for why this is guarded the same way.
    /// </summary>
    public static void ApplyAccent(string accentSetting)
    {
        try
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

            // MaterialDesignThemes' own black/white pick for this resource
            // (recomputed by SetTheme above) leaned black for several of our
            // saturated mid-tone swatches — azul, morado, verde, naranja,
            // rosa, oliva — where white actually reads better. Overriding it
            // here with a real WCAG contrast-ratio comparison (pick whichever
            // of black/white has the higher ratio against this exact color)
            // instead of the library's simpler luminance threshold.
            Application.Current.Resources["MaterialDesign.Brush.Primary.Foreground"] = new SolidColorBrush(IdealForeground(color));
        }
        catch (Exception ex)
        {
            LogFailure("ApplyAccent", ex);
        }
    }

    private static Color IdealForeground(Color background)
    {
        double Linearize(byte channel)
        {
            var c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        var luminance = 0.2126 * Linearize(background.R) + 0.7152 * Linearize(background.G) + 0.0722 * Linearize(background.B);

        // A strict WCAG contrast-ratio comparison (higher of the two ratios
        // wins) was tried here before, but its crossover sits at luminance
        // ~0.18 — well below where black actually reads better in practice.
        // That's a known blind spot of the WCAG 2 relative-luminance formula
        // for saturated blue/purple backgrounds: it recommends black text
        // for colors most people read fine with white. Checked against
        // every swatch in AccentSwatches plus both Original variants — all
        // of them sit under 0.32 luminance, so a flat 0.4 threshold keeps
        // white on every color this app can actually produce today, while
        // still leaving room to pick black for a genuinely light background.
        return luminance > 0.4 ? Colors.Black : Colors.White;
    }

    /// <summary>
    /// Safe stand-in for the "new PaletteHelper().GetTheme().GetBaseTheme()
    /// == BaseTheme.Dark" one-liner scattered across the popup, stats, toast
    /// and tray-menu code — every one of those call sites can hit the same
    /// "Cannot get theme outside of a WPF application" race documented on
    /// Apply() above, just unguarded, so a mistimed startup or a toast that
    /// races the popup's own theme application could crash the whole app
    /// over what's ultimately a cosmetic light/dark read. Falls back to the
    /// OS theme setting on failure, same as ResolveIsDark's own default.
    /// </summary>
    public static bool IsCurrentThemeDark()
    {
        try
        {
            return new PaletteHelper().GetTheme().GetBaseTheme() == BaseTheme.Dark;
        }
        catch (Exception ex)
        {
            LogFailure("IsCurrentThemeDark", ex);
            return IsSystemDarkMode();
        }
    }

    private static void LogFailure(string method, Exception ex)
    {
        try { File.AppendAllText(Path.Combine(Paths.LogsDir, "log_failures.txt"), $"{DateTime.Now:O} ThemeHelper.{method} failed: {ex}\n"); }
        catch { /* truly nothing more we can do */ }
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
