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
    private enum StatsRange { Today, Yesterday, Week, Month }
    private enum PromptDisplayMode { New, Total }

    // Floor so charts never get squished unreadable when many services are
    // stacked in a short window — beyond this point the outer ScrollViewer
    // takes over again, same as before this got responsive.
    private const double MinChartHeight = 90;
    private const double AnchorGap = 12;
    // Wider than the popup-matching width, per the user's request — kept
    // relative to the popup's own width so it scales sensibly regardless
    // of DPI/monitor. Bumped another 50% on top of that (1.6 -> 2.4) to fit
    // the per-agent dashboard cards' stats on one row instead of stacked.
    private const double WidthMultiplier = 2.4;
    // The default height used to match the popup's exactly; once the range
    // tabs, prompt-mode toggle, and dashboard rows were added, that no
    // longer left enough room for even one chart without scrolling — this
    // is extra headroom for those rows, on top of the popup-matched height.
    private const double ExtraDefaultHeight = 170;

    // "Grandecita, pero no enorme" — a comfortable working size well short
    // of the monitor's full work area, not a real OS maximize.
    private const double MaximizedWidthFraction = 0.78;
    private const double MaximizedHeightFraction = 0.82;
    private const double MaxMaximizedWidth = 1100;
    private const double MaxMaximizedHeight = 850;

    private const double MinZoom = 1.0;
    private const double MaxZoom = 4.0;
    private const double ZoomStep = 0.25;

    private readonly List<string> _serviceNames;
    private readonly UsageHistoryStore _historyStore;
    private readonly PromptCountStore _promptCountStore;

    private readonly double _defaultWidth;
    private readonly double _defaultHeight;
    private readonly Rect _anchorBounds;

    private DispatcherTimer? _resizeDebounceTimer;
    private double _zoomLevel = MinZoom;
    private StatsRange _range = StatsRange.Today;
    private PromptDisplayMode _promptMode = PromptDisplayMode.New;

    private bool _isMaximized;
    private double _preMaximizeWidth, _preMaximizeHeight, _preMaximizeLeft, _preMaximizeTop;

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
    /// resize freely. These are only the UNCLAMPED preferences though — the
    /// window can end up bigger than a monitor's work area (especially
    /// after the maximize feature raised the default height), so
    /// <see cref="OnSourceInitialized"/> clamps the real Width/Height/Left/Top
    /// once a window handle (and therefore DPI/monitor info) actually
    /// exists, the same way <see cref="OnMaximizeClick"/> already does.
    /// </summary>
    public StatsWindow(List<string> serviceNames, UsageHistoryStore historyStore, PromptCountStore promptCountStore, Rect anchorBounds)
    {
        InitializeComponent();
        _serviceNames = serviceNames;
        _historyStore = historyStore;
        _promptCountStore = promptCountStore;
        _anchorBounds = anchorBounds;
        Title = Strings.T("stats.title");

        _defaultWidth = Math.Max(MinWidth, anchorBounds.Width * WidthMultiplier);
        _defaultHeight = Math.Max(MinHeight, anchorBounds.Height + ExtraDefaultHeight);

        Width = _defaultWidth;
        Height = _defaultHeight;
        Left = anchorBounds.Left - _defaultWidth - AnchorGap;
        Top = anchorBounds.Top;

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

        FitToWorkArea();
    }

    /// <summary>
    /// The constructor's Width/Height/Left/Top are just a preference
    /// ("as wide/tall as the popup suggests, sitting just to its left") —
    /// on a small or oddly-shaped monitor that can land the window partly
    /// or fully off-screen, which real window placement must never do.
    /// This runs once the window has an hwnd (so DPI/monitor lookups are
    /// reliable, same as <see cref="OnMaximizeClick"/>) and re-derives a
    /// position that (a) fits entirely within the monitor's work area
    /// under <see cref="_anchorBounds"/> and (b) still avoids the popup —
    /// preferring the left side, falling back to the right side, and only
    /// overlapping as an absolute last resort on a monitor too small for
    /// both windows side by side.
    /// </summary>
    private void FitToWorkArea()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var anchorCenterPhysical = new NativeScreenHelper.POINT
        {
            X = (int)((_anchorBounds.Left + _anchorBounds.Width / 2) * dpi.DpiScaleX),
            Y = (int)((_anchorBounds.Top + _anchorBounds.Height / 2) * dpi.DpiScaleY),
        };
        var workArea = NativeScreenHelper.GetWorkAreaForPoint(anchorCenterPhysical);
        var workLeft = workArea.Left / dpi.DpiScaleX;
        var workTop = workArea.Top / dpi.DpiScaleY;
        var workRight = workArea.Right / dpi.DpiScaleX;
        var workBottom = workArea.Bottom / dpi.DpiScaleY;

        var width = Math.Min(Width, workRight - workLeft);
        var height = Math.Min(Height, workBottom - workTop);

        var leftOfAnchor = _anchorBounds.Left - width - AnchorGap;
        var rightOfAnchor = _anchorBounds.Right + AnchorGap;
        double left;
        if (leftOfAnchor >= workLeft)
            left = leftOfAnchor;
        else if (rightOfAnchor + width <= workRight)
            left = rightOfAnchor;
        else
            left = Math.Max(workLeft, Math.Min(Left, workRight - width));

        var top = Math.Max(workTop, Math.Min(Top, workBottom - height));

        Width = width;
        Height = height;
        Left = left;
        Top = top;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Not a real OS maximize (no fullscreen) — "grandecita, pero no
    /// enorme": a comfortable fixed fraction of whatever monitor the
    /// window currently sits on, centered there. Toggling again restores
    /// whatever size/position the window had right before, the same way
    /// a normal maximize/restore button behaves.
    /// </summary>
    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (_isMaximized)
        {
            Width = _preMaximizeWidth;
            Height = _preMaximizeHeight;
            Left = _preMaximizeLeft;
            Top = _preMaximizeTop;
            _isMaximized = false;
        }
        else
        {
            _preMaximizeWidth = Width;
            _preMaximizeHeight = Height;
            _preMaximizeLeft = Left;
            _preMaximizeTop = Top;

            var dpi = VisualTreeHelper.GetDpi(this);
            var centerPhysical = new NativeScreenHelper.POINT
            {
                X = (int)((Left + Width / 2) * dpi.DpiScaleX),
                Y = (int)((Top + Height / 2) * dpi.DpiScaleY),
            };
            var workArea = NativeScreenHelper.GetWorkAreaForPoint(centerPhysical);
            var workLeftDip = workArea.Left / dpi.DpiScaleX;
            var workTopDip = workArea.Top / dpi.DpiScaleY;
            var workWidthDip = (workArea.Right - workArea.Left) / dpi.DpiScaleX;
            var workHeightDip = (workArea.Bottom - workArea.Top) / dpi.DpiScaleY;

            var targetWidth = Math.Min(workWidthDip * MaximizedWidthFraction, MaxMaximizedWidth);
            var targetHeight = Math.Min(workHeightDip * MaximizedHeightFraction, MaxMaximizedHeight);

            Width = targetWidth;
            Height = targetHeight;
            Left = workLeftDip + (workWidthDip - targetWidth) / 2;
            Top = workTopDip + (workHeightDip - targetHeight) / 2;
            _isMaximized = true;
        }
        Render();
    }

    /// <summary>
    /// Deliberately raw points for every range, not hourly/daily averages —
    /// usage climbs then drops sharply on a reset, and averaging across
    /// that would smooth the drop into a misleading mid-value, hiding
    /// exactly the sawtooth shape (and reset timing) this window exists to
    /// show. Point counts stay easily renderable even at Month with the
    /// slowest allowed refresh interval (5 min): well under 10k points.
    /// Every range except Yesterday is open-ended (from its start through
    /// now), so Until is null everywhere but there — Yesterday is the one
    /// case that needs an upper bound too, or it would just show "since
    /// yesterday midnight," i.e. today's data as well.
    /// </summary>
    private static (DateTimeOffset Since, DateTimeOffset? Until) RangeBoundsFor(StatsRange range)
    {
        var now = DateTimeOffset.Now;
        var todayStart = new DateTimeOffset(now.Date, now.Offset);
        return range switch
        {
            StatsRange.Today => (todayStart, null),
            StatsRange.Yesterday => (todayStart.AddDays(-1), todayStart),
            StatsRange.Week => (now.AddDays(-7), null),
            StatsRange.Month => (now.AddDays(-30), null),
            _ => (now.AddDays(-1), null),
        };
    }

    private void BuildRangeSelector(Brush textPrimary, Brush textSecondary, Brush accent)
    {
        RangeSelectorHost.Children.Clear();
        RangeSelectorHost.Children.Add(BuildRangeTab(Strings.T("stats.range.today"), StatsRange.Today, textSecondary, accent));
        RangeSelectorHost.Children.Add(BuildRangeTab(Strings.T("stats.range.yesterday"), StatsRange.Yesterday, textSecondary, accent));
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
    /// Nuevos (per-interval deltas, the default) vs Totales (raw cumulative
    /// count) for the purple prompt-count overlay — same tab visuals as the
    /// range selector, just smaller and right-aligned in the same row so it
    /// doesn't need a whole extra row of its own.
    /// </summary>
    private void BuildPromptModeSelector(Brush textSecondary, Brush promptLineBrush)
    {
        PromptModeSelectorHost.Children.Clear();
        PromptModeSelectorHost.Children.Add(BuildPromptModeTab(Strings.T("stats.promptmode.new"), PromptDisplayMode.New, textSecondary, promptLineBrush));
        PromptModeSelectorHost.Children.Add(BuildPromptModeTab(Strings.T("stats.promptmode.total"), PromptDisplayMode.Total, textSecondary, promptLineBrush));
    }

    private FrameworkElement BuildPromptModeTab(string text, PromptDisplayMode mode, Brush textSecondary, Brush promptLineBrush)
    {
        var isActive = _promptMode == mode;
        var activeBg = promptLineBrush.Clone();
        activeBg.Opacity = 0.14;

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(6, 0, 0, 0),
            Background = isActive ? activeBg : Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = isActive ? FontWeights.Medium : FontWeights.Normal,
                Foreground = isActive ? promptLineBrush : textSecondary,
            },
        };
        border.MouseLeftButtonUp += (s, e) =>
        {
            if (_promptMode == mode) return;
            _promptMode = mode;
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

        // A subtly cooler/lighter gray than the regular card background —
        // just enough to tell the always-on Totales cards apart from the
        // active-tab-scoped ones at a glance, without introducing a whole
        // new color into the window.
        var totalsCardBackground = isDark
            ? new SolidColorBrush(Color.FromRgb(0x4C, 0x4C, 0x56))
            : new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xEF));

        var accent = (Brush)FindResource("MaterialDesign.Brush.Primary");
        var fillBrush = accent.Clone();
        fillBrush.Opacity = 0.16;

        // A violet reads clearly against both the light (#FAFAFA) and dark
        // (#2B2B2E) chart backgrounds, and doesn't collide with the
        // green/amber/red usage gradient or the accent color used
        // elsewhere — important since this line needs to stay visually
        // distinct from the primary series it's overlaid on. Defined here
        // (rather than down by BuildServiceBlocks, where it used to live)
        // since the mode selector tab now needs the same color too.
        var promptLineBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6));

        TitleTextBlock.Text = Strings.T("stats.title");
        TitleTextBlock.Foreground = textPrimary;
        CloseGlyph.Foreground = textPrimary;
        // E922 "Maximize" / E923 "Restore" — same pair of glyphs a normal
        // Windows title bar swaps between.
        MaximizeGlyph.Text = _isMaximized ? "" : "";
        MaximizeGlyph.Foreground = textSecondary;
        MaximizeButton.ToolTip = Strings.T(_isMaximized ? "stats.restore" : "stats.maximize");

        BuildRangeSelector(textPrimary, textSecondary, accent);
        BuildPromptModeSelector(textSecondary, promptLineBrush);

        var (since, until) = RangeBoundsFor(_range);
        var dashboard = BuildDashboardRow(textPrimary, textSecondary, gridBrush, totalsCardBackground, since, until);
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

        // A hardcoded "how much space does the chrome around a chart take"
        // constant kept drifting out of sync every time something (a
        // legend row, the bars sub-panel) was added around the chart, which
        // is exactly how this window ended up permanently needing a
        // scrollbar no matter how big it was made. Instead, build the real
        // blocks once at a throwaway nominal height purely to MEASURE that
        // overhead with real fonts/DPI/data, then build them again — for
        // real this time — at the height that precisely fills what's left.
        const double probeChartHeight = MinChartHeight;
        var probeBlocks = ChartBuilder.BuildServiceBlocks(_serviceNames, _historyStore, since, until,
            chartWidth, probeChartHeight, textPrimary, textSecondary, accent, fillBrush, gridBrush,
            viewportWidth, OnChartZoom, _promptCountStore, promptLineBrush, _promptMode == PromptDisplayMode.Total);
        ContentHost.Children.Add(probeBlocks);
        ContentHost.UpdateLayout();
        var chromeOverhead = Math.Max(0, probeBlocks.ActualHeight - probeChartHeight * _serviceNames.Count);
        ContentHost.Children.Remove(probeBlocks);

        var availableForCharts = viewportHeight - ContentHost.Margin.Top - ContentHost.Margin.Bottom - dashboardHeight - chromeOverhead;
        var chartHeight = Math.Max(MinChartHeight, availableForCharts / _serviceNames.Count);

        var blocks = ChartBuilder.BuildServiceBlocks(_serviceNames, _historyStore, since, until,
            chartWidth, chartHeight, textPrimary, textSecondary, accent, fillBrush, gridBrush,
            viewportWidth, OnChartZoom, _promptCountStore, promptLineBrush, _promptMode == PromptDisplayMode.Total);
        ContentHost.Children.Add(blocks);
    }

    // Agents in a fixed order, matching the /proyectos sections — Claude
    // Code first since this app started as a Claude-focused tool.
    private static readonly string[] DashboardAgents = { "Claude Code", "Codex" };

    private static List<AgentTask> GetAgentTasks(string agent) => agent switch
    {
        "Claude Code" => ClaudeCodeProjectsHelper.GetRecentTasks(2000),
        "Codex" => CodexProjectsHelper.GetRecentTasks(2000),
        _ => new List<AgentTask>(),
    };

    /// <summary>
    /// One row containing BOTH the always-on full-history card and the
    /// active-tab-scoped card per coding agent, side by side — a prior
    /// version split these into two separate stacked rows (one per
    /// section), which wasn't what was asked for: the CARDS go in a row,
    /// not their own contents. Each card's title carries a small scope
    /// label ("Totales" vs the active Hoy/Semana/Mes tab's own name) so two
    /// same-named agent cards sitting next to each other stay distinguishable.
    /// Skips a card entirely when that agent has nothing to show for that
    /// scope; returns null when nothing qualifies at all, so Render() can
    /// skip adding an empty row.
    /// </summary>
    private FrameworkElement? BuildDashboardRow(Brush textPrimary, Brush textSecondary, Brush cardBackground, Brush totalsCardBackground, DateTimeOffset since, DateTimeOffset? until)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        var totalsLabel = Strings.T("stats.dashboard.totals.badge");
        var rangeLabel = Strings.T(_range switch
        {
            StatsRange.Today => "stats.range.today",
            StatsRange.Yesterday => "stats.range.yesterday",
            StatsRange.Week => "stats.range.week",
            StatsRange.Month => "stats.range.month",
            _ => "stats.range.today",
        });

        // All range-scoped cards first, then all Totales cards — grouped by
        // scope rather than interleaved per agent, so the row reads as two
        // clear clusters instead of alternating back and forth.
        var tasksByAgent = DashboardAgents.ToDictionary(a => a, GetAgentTasks);

        foreach (var agent in DashboardAgents)
        {
            var inRange = tasksByAgent[agent].Where(t => t.LastActivity >= since && (until is null || t.LastActivity < until)).ToList();
            var rangeProjectCount = inRange.Select(t => t.ProjectPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var rangeTaskCount = inRange.Count;
            var rangePromptCount = _promptCountStore.GetAgentTotalInRange(agent, since, until);
            if (!(rangePromptCount == 0 && rangeProjectCount == 0 && rangeTaskCount == 0))
                wrap.Children.Add(BuildDashboardCard(agent, rangeLabel, rangePromptCount, rangeProjectCount, rangeTaskCount, textPrimary, textSecondary, cardBackground));
        }

        foreach (var agent in DashboardAgents)
        {
            var tasks = tasksByAgent[agent];
            var totalProjectCount = tasks.Select(t => t.ProjectPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var totalTaskCount = tasks.Count;
            // Latest known cumulative total per project — each snapshot
            // already holds a full-transcript scan, so the latest one IS
            // the all-time total (see PromptCountStore/GetPromptCountsByProject),
            // not a delta sum.
            var totalPromptCount = _promptCountStore.GetLatestTotalsByProject(agent).Values.Sum();
            if (!(totalPromptCount == 0 && totalProjectCount == 0 && totalTaskCount == 0))
                wrap.Children.Add(BuildDashboardCard(agent, totalsLabel, totalPromptCount, totalProjectCount, totalTaskCount, textPrimary, textSecondary, totalsCardBackground));
        }

        return wrap.Children.Count > 0 ? wrap : null;
    }

    private static Border BuildDashboardCard(string agent, string scopeLabel, int promptCount, int projectCount, int taskCount,
        Brush textPrimary, Brush textSecondary, Brush cardBackground)
    {
        var stack = new StackPanel();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        titleRow.Children.Add(new TextBlock
        {
            Text = AgentDisplayNames.For(agent),
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = textPrimary,
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = $"  ·  {scopeLabel}",
            FontSize = 10,
            Foreground = textSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(titleRow);
        stack.Children.Add(BuildDashboardStatLine("✨", Strings.F("stats.dashboard.prompts", promptCount), textSecondary));
        stack.Children.Add(BuildDashboardStatLine("🗂️", Strings.F("stats.dashboard.projects", projectCount), textSecondary));
        stack.Children.Add(BuildDashboardStatLine("💬", Strings.F("stats.dashboard.tasks", taskCount), textSecondary));

        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 10, 10),
            MinWidth = 150,
            Background = cardBackground,
            Child = stack,
        };
    }

    private static FrameworkElement BuildDashboardStatLine(string glyph, string text, Brush foreground)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        row.Children.Add(new TextBlock { Text = glyph, FontSize = 11, Margin = new Thickness(0, 0, 6, 0) });
        row.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = foreground });
        return row;
    }
}
