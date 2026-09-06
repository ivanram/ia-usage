using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClaudeUsageTray;

/// <summary>
/// A small mini-mockup button used by the palette picker in SettingsWindow:
/// its own background, an inset "card", an accent dot and two text bars, so
/// a whole scheme can be judged before it's applied — same idea as the
/// existing ColorSwatchButton style, just with four colors instead of one.
/// Selection uses the same Tag == "Selected" convention ColorSwatchButton
/// already relies on (see SettingsWindow.xaml.cs's SelectAccent).
/// </summary>
public sealed class PaletteChipButton : Button
{
    public static readonly DependencyProperty ChipBgProperty =
        DependencyProperty.Register(nameof(ChipBg), typeof(Brush), typeof(PaletteChipButton));
    public static readonly DependencyProperty ChipSurfaceProperty =
        DependencyProperty.Register(nameof(ChipSurface), typeof(Brush), typeof(PaletteChipButton));
    public static readonly DependencyProperty ChipAccentDotProperty =
        DependencyProperty.Register(nameof(ChipAccentDot), typeof(Brush), typeof(PaletteChipButton));
    public static readonly DependencyProperty ChipTextProperty =
        DependencyProperty.Register(nameof(ChipText), typeof(Brush), typeof(PaletteChipButton));

    public Brush ChipBg
    {
        get => (Brush)GetValue(ChipBgProperty);
        set => SetValue(ChipBgProperty, value);
    }

    public Brush ChipSurface
    {
        get => (Brush)GetValue(ChipSurfaceProperty);
        set => SetValue(ChipSurfaceProperty, value);
    }

    public Brush ChipAccentDot
    {
        get => (Brush)GetValue(ChipAccentDotProperty);
        set => SetValue(ChipAccentDotProperty, value);
    }

    public Brush ChipText
    {
        get => (Brush)GetValue(ChipTextProperty);
        set => SetValue(ChipTextProperty, value);
    }
}
