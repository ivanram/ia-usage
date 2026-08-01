using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MaterialDesignThemes.Wpf;

namespace ClaudeUsageTray;

public partial class StatsWindow : Window
{
    private const double ChartHeight = 130;
    private const double AnchorGap = 12;
    // "50% más ancha" than the old popup-matching width, per the user's
    // request — kept relative to the popup's own width so it scales
    // sensibly regardless of DPI/monitor.
    private const double WidthMultiplier = 1.5;

    private readonly List<string> _serviceNames;
    private readonly UsageHistoryStore _historyStore;

    private readonly double _defaultWidth;
    private readonly double _defaultHeight;
    private readonly double _defaultLeft;
    private readonly double _defaultTop;

    private DispatcherTimer? _resizeDebounceTimer;

    // Same reasoning as SettingsWindow's taskbar icon fields — built once
    // and kept alive for the window's lifetime, sent to Windows via
    // WM_SETICON since a WindowChrome-customized window doesn't reliably
    // pick up the taskbar icon any other way.
    private readonly System.Drawing.Icon _smallTaskbarIcon = IconFactory.BuildRobotIcon(16);
    private readonly System.Drawing.Icon _bigTaskbarIcon = IconFactory.BuildRobotIcon(32);

    /// <summary>
    /// <paramref name="anchorBounds"/> is the main popup's on-screen bounds
    /// at the moment Stats was opened from it — Stats opens just to its
    /// left by default, matching the popup's height exactly and sitting
    /// flush with its top edge, at a starting width the user can then
    /// resize freely (see ResetSizeButton for getting back to this default).
    /// </summary>
    public StatsWindow(List<string> serviceNames, UsageHistoryStore historyStore, Rect anchorBounds)
    {
        InitializeComponent();
        _serviceNames = serviceNames;
        _historyStore = historyStore;
        Title = Strings.T("stats.title");

        _defaultWidth = Math.Max(MinWidth, anchorBounds.Width * WidthMultiplier);
        _defaultHeight = Math.Max(MinHeight, anchorBounds.Height);
        _defaultLeft = anchorBounds.Left - _defaultWidth - AnchorGap;
        _defaultTop = anchorBounds.Top;

        Width = _defaultWidth;
        Height = _defaultHeight;
        Left = _defaultLeft;
        Top = _defaultTop;

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        Render();
    }

    /// <summary>
    /// Debounced so dragging an edge doesn't rebuild the whole chart (with
    /// its spline math and gridline layout) on every single pixel of
    /// movement — only once the user pauses for a moment.
    /// </summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded) return;

        _resizeDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _resizeDebounceTimer.Stop();
        _resizeDebounceTimer.Tick -= OnResizeDebounceTick;
        _resizeDebounceTimer.Tick += OnResizeDebounceTick;
        _resizeDebounceTimer.Start();
    }

    private void OnResizeDebounceTick(object? sender, EventArgs e)
    {
        _resizeDebounceTimer!.Stop();
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

        // ContentHost is a ScrollViewer child with horizontal scrolling
        // disabled, so its ActualWidth already tracks the window's current
        // available content width — reading it here (instead of a fixed
        // constant) is what makes the chart actually redraw wider/narrower
        // when the user resizes the window.
        var chartWidth = ContentHost.ActualWidth > 0 ? ContentHost.ActualWidth : _defaultWidth - 40;

        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var blocks = ChartBuilder.BuildServiceBlocks(_serviceNames, _historyStore, since,
            chartWidth, ChartHeight, textPrimary, textSecondary, accent, fillBrush, gridBrush);
        ContentHost.Children.Add(blocks);
    }
}
