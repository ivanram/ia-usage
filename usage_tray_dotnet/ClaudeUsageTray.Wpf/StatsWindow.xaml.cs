using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace ClaudeUsageTray;

public partial class StatsWindow : Window
{
    // "50% más ancha" than the old single-column, popup-matching width
    // (288 content + 76 chrome = 364) — two of these plus the gap and the
    // content margins lands close to that target while comfortably fitting
    // two services per row.
    private const double ColumnWidth = 240;
    private const double ColumnGap = 16;
    private const double ChartHeight = 130;
    private const double AnchorGap = 12;
    private const double DefaultHeight = 420;

    private readonly List<string> _serviceNames;
    private readonly UsageHistoryStore _historyStore;

    private readonly double _defaultWidth;
    private readonly double _defaultHeight;
    private readonly double _defaultLeft;
    private readonly double _defaultTop;

    // Same reasoning as SettingsWindow's taskbar icon fields — built once
    // and kept alive for the window's lifetime, sent to Windows via
    // WM_SETICON since a WindowChrome-customized window doesn't reliably
    // pick up the taskbar icon any other way.
    private readonly System.Drawing.Icon _smallTaskbarIcon = IconFactory.BuildRobotIcon(16);
    private readonly System.Drawing.Icon _bigTaskbarIcon = IconFactory.BuildRobotIcon(32);

    /// <summary>
    /// <paramref name="anchorBounds"/> is the main popup's on-screen bounds
    /// at the moment Stats was opened from it — Stats opens just to its
    /// left by default, at a fixed starting size the user can then resize
    /// freely (see ResetSizeButton for getting back to this default).
    /// </summary>
    public StatsWindow(List<string> serviceNames, UsageHistoryStore historyStore, Rect anchorBounds)
    {
        InitializeComponent();
        _serviceNames = serviceNames;
        _historyStore = historyStore;
        Title = Strings.T("stats.title");

        var columns = Math.Max(1, _serviceNames.Count);
        var contentWidth = columns * ColumnWidth + Math.Max(0, columns - 1) * ColumnGap;
        _defaultWidth = Math.Max(MinWidth, contentWidth + 40);
        _defaultHeight = DefaultHeight;
        _defaultLeft = anchorBounds.Left - _defaultWidth - AnchorGap;
        _defaultTop = anchorBounds.Top;

        Width = _defaultWidth;
        Height = _defaultHeight;
        Left = _defaultLeft;
        Top = _defaultTop;

        Render();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        DwmHelper.EnableRoundedCorners(hwnd);
        DwmHelper.SetWindowIcon(hwnd, _smallTaskbarIcon.Handle, _bigTaskbarIcon.Handle);
        var isDark = new PaletteHelper().GetTheme().GetBaseTheme() == BaseTheme.Dark;
        DwmHelper.SetTitleBarDarkMode(hwnd, isDark);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnResetSizeClick(object sender, RoutedEventArgs e)
    {
        Width = _defaultWidth;
        Height = _defaultHeight;
        Left = _defaultLeft;
        Top = _defaultTop;
    }

    /// <summary>
    /// Same hand-picked colors as PopupWindow.ApplyThemeColors() — not the
    /// MaterialDesignPaper/Body resources, which read as a subtly different
    /// (slightly whiter/flatter) shade than the popup's own background and
    /// made the two windows look like they didn't belong to the same app
    /// sitting right next to each other.
    /// </summary>
    private void Render()
    {
        ContentHost.Children.Clear();

        var isDark = new PaletteHelper().GetTheme().GetBaseTheme() == BaseTheme.Dark;

        RootGrid.Background = isDark
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

        TitleTextBlock.Text = Strings.T("stats.title");
        TitleTextBlock.Foreground = textPrimary;
        ResetSizeGlyph.Foreground = textSecondary;
        CloseGlyph.Foreground = textPrimary;

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

        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var columns = ChartBuilder.BuildServiceColumns(_serviceNames, _historyStore, since,
            ColumnWidth, ChartHeight, ColumnGap, textPrimary, textSecondary, accent, fillBrush, gridBrush);
        ContentHost.Children.Add(columns);
    }
}
