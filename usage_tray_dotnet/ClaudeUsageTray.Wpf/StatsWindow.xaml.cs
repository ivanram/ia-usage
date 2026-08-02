using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MaterialDesignThemes.Wpf;

namespace ClaudeUsageTray;

public partial class StatsWindow : Window
{
    private enum StatsRange { Today, Week, Month }

    // Floor so charts never get squished unreadable when many services are
    // stacked in a short window — beyond this point the outer ScrollViewer
    // takes over again, same as before this got responsive.
    private const double MinChartHeight = 90;
    // Estimate of everything BuildServiceBlocks draws around a chart itself
    // (icon+name header row + its bottom margin, plus the block's own
    // bottom margin) — used to work out how much of the window's actual
    // height is left for the charts themselves. Approximate on purpose:
    // being a few pixels off just means an almost-imperceptible sliver of
    // scroll slack, not a layout bug.
    private const double PerServiceChromeHeight = 48;
    private const double AnchorGap = 12;
    // "50% más ancha" than the old popup-matching width, per the user's
    // request — kept relative to the popup's own width so it scales
    // sensibly regardless of DPI/monitor.
    private const double WidthMultiplier = 1.5;

    private const double MinZoom = 1.0;
    private const double MaxZoom = 4.0;
    private const double ZoomStep = 0.25;

    private readonly List<string> _serviceNames;
    private readonly UsageHistoryStore _historyStore;
    private readonly PromptCountStore _promptCountStore;

    private readonly double _defaultWidth;
    private readonly double _defaultHeight;
    private readonly double _defaultLeft;
    private readonly double _defaultTop;

    private DispatcherTimer? _resizeDebounceTimer;
    private double _zoomLevel = MinZoom;
    private StatsRange _range = StatsRange.Today;

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
    public StatsWindow(List<string> serviceNames, UsageHistoryStore historyStore, PromptCountStore promptCountStore, Rect anchorBounds)
    {
        InitializeComponent();
        _serviceNames = serviceNames;
        _historyStore = historyStore;
        _promptCountStore = promptCountStore;
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

    /// <summary>
    /// Wired to every chart's mouse wheel (see ChartBuilder.BuildServiceBlocks) —
    /// one shared zoom level for all services keeps them comparable/aligned
    /// instead of each drifting to its own scale.
    /// </summary>
    private void OnChartZoom(int direction)
    {
        var newZoom = Math.Clamp(_zoomLevel + direction * ZoomStep, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _zoomLevel) < 0.001) return;
        _zoomLevel = newZoom;
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
        _zoomLevel = MinZoom;
        Render();
    }

    /// <summary>
    /// Deliberately raw points for every range, not hourly/daily averages —
    /// usage climbs then drops sharply on a reset, and averaging across
    /// that would smooth the drop into a misleading mid-value, hiding
    /// exactly the sawtooth shape (and reset timing) this window exists to
    /// show. Point counts stay easily renderable even at Month with the
    /// slowest allowed refresh interval (5 min): well under 10k points.
    /// </summary>
    private static DateTimeOffset SinceForRange(StatsRange range)
    {
        var now = DateTimeOffset.Now;
        return range switch
        {
            StatsRange.Today => new DateTimeOffset(now.Date, now.Offset),
            StatsRange.Week => now.AddDays(-7),
            StatsRange.Month => now.AddDays(-30),
            _ => now.AddDays(-1),
        };
    }

    private void BuildRangeSelector(Brush textPrimary, Brush textSecondary, Brush accent)
    {
        RangeSelectorHost.Children.Clear();
        RangeSelectorHost.Children.Add(BuildRangeTab(Strings.T("stats.range.today"), StatsRange.Today, textSecondary, accent));
        RangeSelectorHost.Children.Add(BuildRangeTab(Strings.T("stats.range.week"), StatsRange.Week, textSecondary, accent));
        RangeSelectorHost.Children.Add(BuildRangeTab(Strings.T("stats.range.month"), StatsRange.Month, textSecondary, accent));
    }

    private FrameworkElement BuildRangeTab(string text, StatsRange range, Brush textSecondary, Brush accent)
    {
        var isActive = _range == range;
        var activeBg = accent.Clone();
        activeBg.Opacity = 0.14;

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 6, 0),
            Background = isActive ? activeBg : Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = isActive ? FontWeights.Medium : FontWeights.Normal,
                Foreground = isActive ? accent : textSecondary,
            },
        };
        border.MouseLeftButtonUp += (s, e) =>
        {
            if (_range == range) return;
            _range = range;
            Render();
        };
        return border;
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

        BuildRangeSelector(textPrimary, textSecondary, accent);

        var since = SinceForRange(_range);
        var dashboard = BuildDashboard(textPrimary, textSecondary, gridBrush, since);
        var dashboardHeight = 0.0;
        if (dashboard is not null)
        {
            ContentHost.Children.Add(dashboard);
            // Force a layout pass now so its real height is known before
            // the chart-height budget below is computed — otherwise the
            // dashboard's own space wouldn't be accounted for and charts
            // could overflow the viewport again, right back to the
            // scrollbar this whole responsive-sizing pass was meant to fix.
            ContentHost.UpdateLayout();
            dashboardHeight = dashboard.ActualHeight;
        }

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
        // when the user resizes the window. Zooming in scales the chart's
        // own (content) width past that viewport, which is what gives each
        // chart's own horizontal scrollbar something to do.
        var viewportWidth = ContentHost.ActualWidth > 0 ? ContentHost.ActualWidth : _defaultWidth - 40;
        var chartWidth = viewportWidth * _zoomLevel;

        // Same idea as the width, but for height: split whatever vertical
        // room the ScrollViewer's own viewport actually has among the
        // stacked service blocks, instead of a fixed height that left
        // spare room unused in a tall window and forced a scrollbar in a
        // short one.
        var viewportHeight = ContentScrollViewer.ActualHeight > 0 ? ContentScrollViewer.ActualHeight : _defaultHeight - 76;
        var availableForCharts = viewportHeight - ContentHost.Margin.Top - ContentHost.Margin.Bottom - dashboardHeight
            - PerServiceChromeHeight * _serviceNames.Count;
        var chartHeight = Math.Max(MinChartHeight, availableForCharts / _serviceNames.Count);

        // A violet reads clearly against both the light (#FAFAFA) and dark
        // (#2B2B2E) chart backgrounds, and doesn't collide with the
        // green/amber/red usage gradient or the accent color used
        // elsewhere — important since this line needs to stay visually
        // distinct from the primary series it's overlaid on.
        var promptLineBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6));

        var blocks = ChartBuilder.BuildServiceBlocks(_serviceNames, _historyStore, since,
            chartWidth, chartHeight, textPrimary, textSecondary, accent, fillBrush, gridBrush,
            viewportWidth, OnChartZoom, _promptCountStore, promptLineBrush);
        ContentHost.Children.Add(blocks);
    }

    // Agents in a fixed order, matching the /proyectos sections — Claude
    // Code first since this app started as a Claude-focused tool.
    private static readonly string[] DashboardAgents = { "Claude Code", "Codex" };

    /// <summary>
    /// One compact card per coding agent that has any activity in range:
    /// new prompts (from PromptCountStore), distinct projects touched, and
    /// distinct tasks/chats — the project/task counts are computed live
    /// from the same helpers /proyectos uses (cheap: they only read each
    /// session's first line), not from PromptCountStore, which only ever
    /// tracks prompt totals. Returns null when neither agent has anything
    /// to show yet, so Render() can skip adding an empty row.
    /// </summary>
    private FrameworkElement? BuildDashboard(Brush textPrimary, Brush textSecondary, Brush cardBackground, DateTimeOffset since)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };

        foreach (var agent in DashboardAgents)
        {
            var tasks = agent switch
            {
                "Claude Code" => ClaudeCodeProjectsHelper.GetRecentTasks(2000),
                "Codex" => CodexProjectsHelper.GetRecentTasks(2000),
                _ => new List<AgentTask>(),
            };
            var inRange = tasks.Where(t => t.LastActivity >= since).ToList();
            var projectCount = inRange.Select(t => t.ProjectPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var taskCount = inRange.Count;
            var promptCount = _promptCountStore.GetAgentTotalInRange(agent, since);

            if (promptCount == 0 && projectCount == 0 && taskCount == 0) continue;

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = agent,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = textPrimary,
                Margin = new Thickness(0, 0, 0, 6),
            });
            stack.Children.Add(BuildDashboardStatLine("✨", Strings.F("stats.dashboard.prompts", promptCount), textSecondary));
            stack.Children.Add(BuildDashboardStatLine("🗂️", Strings.F("stats.dashboard.projects", projectCount), textSecondary));
            stack.Children.Add(BuildDashboardStatLine("💬", Strings.F("stats.dashboard.tasks", taskCount), textSecondary));

            wrap.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 10, 10),
                MinWidth = 150,
                Background = cardBackground,
                Child = stack,
            });
        }

        return wrap.Children.Count > 0 ? wrap : null;
    }

    private static FrameworkElement BuildDashboardStatLine(string glyph, string text, Brush foreground)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        row.Children.Add(new TextBlock { Text = glyph, FontSize = 11, Margin = new Thickness(0, 0, 6, 0) });
        row.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = foreground });
        return row;
    }
}
