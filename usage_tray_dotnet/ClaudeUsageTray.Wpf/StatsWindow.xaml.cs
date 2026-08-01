using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace ClaudeUsageTray;

public partial class StatsWindow : Window
{
    // Landscape layout: one column per service side by side, rather than
    // stacked — the window's height is pinned to match the main popup's,
    // so going wide (not tall) is how it fits everyone's chart in without
    // the content getting cramped or clipped.
    private const double ColumnWidth = 190;
    private const double ColumnGap = 16;
    private const double AnchorGap = 12;

    // Same Grid Margin="18" (drop-shadow clearance) + ContentHost
    // Margin="20" scheme PopupWindow uses — kept as the same two numbers
    // here so the two windows' outer chrome matches exactly, not just
    // their colors.
    private const double ChromeMargin = (18 * 2) + (20 * 2);

    // Rough heights of the fixed header content above the charts (title,
    // subtitle, per-column icon+name row) — approximate since asking WPF
    // for the real number would mean a live layout pass, but close enough
    // that the chart area doesn't visibly overshoot the window's fixed
    // height by more than a couple of pixels.
    private const double TitleHeight = 26;
    private const double SubtitleHeight = 34;
    private const double ColumnHeaderHeight = 24;

    private readonly List<string> _serviceNames;
    private readonly UsageHistoryStore _historyStore;

    // Same reasoning as SettingsWindow's taskbar icon fields — built once
    // and kept alive for the window's lifetime, sent to Windows via
    // WM_SETICON since WindowStyle="None" windows don't reliably pick up
    // the taskbar icon any other way.
    private readonly System.Drawing.Icon _smallTaskbarIcon = IconFactory.BuildRobotIcon(16);
    private readonly System.Drawing.Icon _bigTaskbarIcon = IconFactory.BuildRobotIcon(32);

    /// <summary>
    /// <paramref name="anchorBounds"/> is the main popup's on-screen bounds
    /// at the moment Stats was opened from it — Stats matches its height
    /// exactly and opens just to its left. Width/height are computed and
    /// set directly here (not via SizeToContent) so the correct position
    /// is known immediately, with no need to wait for a Loaded pass the
    /// way a content-driven size would require.
    /// </summary>
    public StatsWindow(List<string> serviceNames, UsageHistoryStore historyStore, Rect anchorBounds)
    {
        InitializeComponent();
        _serviceNames = serviceNames;
        _historyStore = historyStore;
        Title = Strings.T("stats.title");

        var contentWidth = _serviceNames.Count == 0
            ? 240
            : _serviceNames.Count * ColumnWidth + Math.Max(0, _serviceNames.Count - 1) * ColumnGap;

        Width = contentWidth + ChromeMargin;
        Height = anchorBounds.Height;
        ContentHost.Width = contentWidth;

        Left = anchorBounds.Left - Width - AnchorGap;
        Top = anchorBounds.Top;

        Render(contentWidth);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // Deliberately NOT calling DwmHelper.EnableRoundedCorners here —
        // this window (like PopupWindow/ToastWindow/AppDialogWindow) draws
        // its own rounded card inside an AllowsTransparency Margin, and
        // asking DWM to ALSO round the actual (invisible, rectangular) HWND
        // is what produced the stray ghost border around the card. Only
        // SettingsWindow uses EnableRoundedCorners, because it's a real
        // opaque window without this transparent-margin trick.
        DwmHelper.SetWindowIcon(hwnd, _smallTaskbarIcon.Handle, _bigTaskbarIcon.Handle);
        var isDark = new PaletteHelper().GetTheme().GetBaseTheme() == BaseTheme.Dark;
        DwmHelper.SetTitleBarDarkMode(hwnd, isDark);
    }

    private void OnRootMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { /* mouse released mid-drag */ }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Same hand-picked colors as PopupWindow.ApplyThemeColors() — not the
    /// MaterialDesignPaper/Body resources, which read as a subtly different
    /// (slightly whiter/flatter) shade than the popup's own background and
    /// made the two windows look like they didn't belong to the same app
    /// sitting right next to each other.
    /// </summary>
    private void Render(double contentWidth)
    {
        ContentHost.Children.Clear();

        var isDark = new PaletteHelper().GetTheme().GetBaseTheme() == BaseTheme.Dark;

        RootBorder.Background = isDark
            ? new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2E))
            : new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));

        var textPrimary = isDark
            ? new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2))
            : new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));

        var textSecondary = isDark
            ? new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xB8))
            : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

        var gridBrush = isDark
            ? new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x48))
            : new SolidColorBrush(Color.FromRgb(0xE2, 0xE2, 0xE2));

        var accent = (Brush)FindResource("MaterialDesign.Brush.Primary");
        var fillBrush = accent.Clone();
        fillBrush.Opacity = 0.16;

        CloseButton.ApplyTemplate();
        if (CloseButton.Template.FindName("CloseGlyph", CloseButton) is TextBlock closeGlyph)
        {
            closeGlyph.Foreground = textPrimary;
        }

        ContentHost.Children.Add(new TextBlock
        {
            Text = Strings.T("stats.title"),
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = textPrimary,
            Margin = new Thickness(0, 0, 0, 4),
        });

        ContentHost.Children.Add(new TextBlock
        {
            Text = Strings.T("stats.subtitle"),
            FontSize = 12,
            Foreground = textSecondary,
            Margin = new Thickness(0, 0, 0, 18),
        });

        if (_serviceNames.Count == 0)
        {
            ContentHost.Children.Add(new TextBlock
            {
                Text = Strings.T("stats.noservices"),
                FontSize = 13,
                Foreground = textSecondary,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        var chartHeight = Math.Max(50, Height - ChromeMargin - TitleHeight - SubtitleHeight - ColumnHeaderHeight);
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var columns = ChartBuilder.BuildServiceColumns(_serviceNames, _historyStore, since,
            ColumnWidth, chartHeight, ColumnGap, textPrimary, textSecondary, accent, fillBrush, gridBrush);
        ContentHost.Children.Add(columns);
    }
}
