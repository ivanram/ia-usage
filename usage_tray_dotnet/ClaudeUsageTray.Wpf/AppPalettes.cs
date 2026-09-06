using System.Linq;
using System.Windows.Media;

namespace ClaudeUsageTray;

/// <summary>
/// Background/surface/text/divider schemes for SettingsWindow and
/// StatsWindow only. Deliberately does not touch accent color — that stays
/// the existing, fully orthogonal ThemeHelper/AccentColor picker. The
/// popup, toast and tray menu never read this; they keep the app's
/// original hardcoded look regardless of what's selected here.
/// </summary>
public sealed record AppearancePalette(
    Brush WindowBg,
    Brush CardBg,
    Brush CardBgAlt,
    Brush Text,
    Brush TextSecondary,
    Brush Divider);

public static class AppPalettes
{
    public const string DefaultId = "default";

    private sealed record PaletteHex(
        string WindowBg, string CardBg, string CardBgAlt, string Text, string TextSecondary, string Divider);

    private sealed record PaletteDefinition(string Id, string Name, PaletteHex Dark, PaletteHex Light);

    // "default" is not in this table — Resolve() special-cases it to the
    // app's real, already-shipping literals (StatsWindow.xaml.cs's own
    // isDark picks, PopupWindow's #FAFAFA/#2B2B2E) so that choice is
    // pixel-identical to today, not merely close to it.
    private static readonly PaletteDefinition[] Alternates =
    {
        new("graphite", "Grafito",
            new PaletteHex("#1A1A1A", "#262626", "#2F2F2F", "#E8E8E8", "#ABABAB", "#343434"),
            new PaletteHex("#F0F0F0", "#FFFFFF", "#E7E7E7", "#1E1E1E", "#4A4A4A", "#D6D6D6")),
        new("frost", "Escarcha",
            new PaletteHex("#1E232D", "#2A3040", "#333A4C", "#E5E9F0", "#AEB6C6", "#343B49"),
            new PaletteHex("#E9EDF3", "#FBFCFE", "#DEE4EE", "#2E3440", "#454E60", "#D3D9E3")),
        new("umber", "Ámbar",
            new PaletteHex("#241F1A", "#302923", "#3A322B", "#E2D7C8", "#B4A897", "#3F372E"),
            new PaletteHex("#F2EADD", "#FBF6EE", "#EAE1D2", "#2A231C", "#574C40", "#DFD3C2")),
    };

    // These two are only ever consumed by StatsWindow (SettingsWindow skips
    // calling Resolve entirely for the "default" id, keeping its original
    // live SetResourceReference path untouched) — so CardBg/CardBgAlt here
    // must match StatsWindow.Render()'s own literals exactly
    // (gridBrush/totalsCardBackground), not SettingsWindow's card color.
    private static readonly PaletteHex DefaultDark = new("#2B2B2E", "#454548", "#4C4C56", "#F2F2F2", "#B8B8B8", "#38383C");
    private static readonly PaletteHex DefaultLight = new("#FAFAFA", "#E2E2E2", "#E6E6EF", "#1A1A1A", "#555555", "#D4D4D5");

    /// <summary>Ordered (id, displayName) pairs for the picker UI — "default" first, then the alternates.</summary>
    public static readonly (string Id, string Name)[] All = BuildAll();

    private static (string, string)[] BuildAll()
    {
        var list = new (string, string)[Alternates.Length + 1];
        list[0] = (DefaultId, "IA Usage");
        for (var i = 0; i < Alternates.Length; i++)
        {
            list[i + 1] = (Alternates[i].Id, Alternates[i].Name);
        }
        return list;
    }

    public static AppearancePalette Resolve(string? paletteId, bool isDark)
    {
        var def = Alternates.FirstOrDefault(p => p.Id == paletteId);
        var hex = def is not null ? (isDark ? def.Dark : def.Light) : (isDark ? DefaultDark : DefaultLight);
        return new AppearancePalette(
            WindowBg: Brush(hex.WindowBg),
            CardBg: Brush(hex.CardBg),
            CardBgAlt: Brush(hex.CardBgAlt),
            Text: Brush(hex.Text),
            TextSecondary: Brush(hex.TextSecondary),
            Divider: Brush(hex.Divider));
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
        brush.Freeze();
        return brush;
    }
}
