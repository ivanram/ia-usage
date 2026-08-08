using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MaterialDesignThemes.Wpf;

namespace ClaudeUsageTray;

public partial class StatsWindow : Window
{
    private enum StatsRange { Today, Yesterday, Week, Month, Custom }
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
    private readonly PromptScanCache _promptScanCache;

    private readonly double _defaultWidth;
    private readonly double _defaultHeight;
    private readonly Rect _anchorBounds;

    private DispatcherTimer? _resizeDebounceTimer;
    private double _zoomLevel = MinZoom;
    private StatsRange _range = StatsRange.Today;
    private PromptDisplayMode _promptMode = PromptDisplayMode.New;
    // The day picked via the "Otra fecha" calendar — only meaningful while
    // _range == StatsRange.Custom, but kept around after leaving that tab
    // so flipping back to it re-shows the same day instead of forgetting it.
    private DateTimeOffset? _customDate;
    private Popup? _datePickerPopup;

    // "Vista de calendario" is a separate display MODE, not another value
    // of _range — it replaces the chart section with a month grid while
    // the dashboard cards above keep responding to _range exactly as
    // before. _calendarMonth is the first-of-month currently shown, kept
    // around across toggles/navigation the same way _customDate is.
    private bool _showCalendar;
    private DateTimeOffset _calendarMonth = new(DateTime.Now.Year, DateTime.Now.Month, 1, 0, 0, 0, DateTimeOffset.Now.Offset);

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
    public StatsWindow(List<string> serviceNames, UsageHistoryStore historyStore, PromptCountStore promptCountStore, PromptScanCache promptScanCache, Rect anchorBounds)
    {
        InitializeComponent();
        _serviceNames = serviceNames;
        _historyStore = historyStore;
        _promptCountStore = promptCountStore;
        _promptScanCache = promptScanCache;
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

    /// <summary>
    /// The chart tabs stay scroll-free by shrinking/growing each chart to
    /// exactly fill whatever height the window already has (see the
    /// chartHeight math above) — but the calendar grid's cell height is
    /// fixed (MinHeight 54 per day), so a 6-row month simply doesn't fit in
    /// a window sized for the chart tabs and falls back to the outer
    /// ScrollViewer. Instead of squeezing calendar cells to fit, this grows
    /// or shrinks the WINDOW itself to match the calendar's real content
    /// height, clamped to the current monitor's work area exactly like
    /// <see cref="FitToWorkArea"/> already does for initial placement.
    /// </summary>
    private void ResizeToFitCalendar()
    {
        // A maximized window already offers generous room; growing/
        // shrinking it here would fight OnMaximizeClick's own sizing and
        // silently "un-maximize" it the next time the calendar re-renders.
        if (_isMaximized) return;

        RangeRow.UpdateLayout();
        var rangeRowHeight = RangeRow.ActualHeight;

        var contentWidth = ContentHost.ActualWidth > 0 ? ContentHost.ActualWidth : Width;
        ContentHost.Measure(new Size(contentWidth, double.PositiveInfinity));
        var contentHeight = ContentHost.DesiredSize.Height;

        const double TitleRowHeight = 36;
        const double ChromeSlack = 4;
        var desiredHeight = TitleRowHeight + rangeRowHeight + contentHeight + ChromeSlack;

        var dpi = VisualTreeHelper.GetDpi(this);
        var centerPhysical = new NativeScreenHelper.POINT
        {
            X = (int)((Left + Width / 2) * dpi.DpiScaleX),
            Y = (int)((Top + Height / 2) * dpi.DpiScaleY),
        };
        var workArea = NativeScreenHelper.GetWorkAreaForPoint(centerPhysical);
        var workTopDip = workArea.Top / dpi.DpiScaleY;
        var workBottomDip = workArea.Bottom / dpi.DpiScaleY;
        var maxHeight = workBottomDip - workTopDip;

        var newHeight = Math.Clamp(desiredHeight, MinHeight, maxHeight);
        if (Math.Abs(newHeight - Height) < 1) return;

        Height = newHeight;
        if (Top + Height > workBottomDip) Top = Math.Max(workTopDip, workBottomDip - Height);
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
    /// Every range except Yesterday and Custom is open-ended (from its start
    /// through now), so Until is null everywhere but there — both need an
    /// upper bound too, or "since that day's midnight" would just keep
    /// pulling in everything more recent as well.
    /// </summary>
    private (DateTimeOffset Since, DateTimeOffset? Until) RangeBoundsFor(StatsRange range)
    {
        var now = DateTimeOffset.Now;
        var todayStart = new DateTimeOffset(now.Date, now.Offset);
        return range switch
        {
            StatsRange.Today => (todayStart, null),
            StatsRange.Yesterday => (todayStart.AddDays(-1), todayStart),
            StatsRange.Week => (now.AddDays(-7), null),
            StatsRange.Month => (now.AddDays(-30), null),
            StatsRange.Custom when _customDate is { } d => (d, d.AddDays(1)),
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
        RangeSelectorHost.Children.Add(BuildCustomDateTab(textSecondary, accent));
        RangeSelectorHost.Children.Add(BuildCalendarViewToggle(textSecondary, accent));
    }

    /// <summary>
    /// Same tab look as the others, but it's a MODE toggle rather than a
    /// value in the same mutually-exclusive group as Hoy/Ayer/Semana/Mes —
    /// switching it on replaces the chart section with a month calendar
    /// (see BuildCalendarView) while the dashboard cards above keep
    /// responding to whatever range tab is separately selected.
    /// </summary>
    private FrameworkElement BuildCalendarViewToggle(Brush textSecondary, Brush accent)
    {
        var isActive = _showCalendar;
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
                Text = Strings.T("stats.calendarview"),
                FontSize = 12,
                FontWeight = isActive ? FontWeights.Medium : FontWeights.Normal,
                Foreground = isActive ? accent : textSecondary,
            },
        };
        border.MouseLeftButtonUp += (s, e) =>
        {
            _showCalendar = !_showCalendar;
            Render();
        };
        return border;
    }

    /// <summary>
    /// Same tab look as BuildRangeTab, but clicking it opens a calendar
    /// popup instead of switching range immediately — Custom needs a day
    /// picked first, there's nothing sensible to switch TO on the bare
    /// click the way there is for Hoy/Ayer/Semana/Mes.
    /// </summary>
    private FrameworkElement BuildCustomDateTab(Brush textSecondary, Brush accent)
    {
        var isActive = _range == StatsRange.Custom && !_showCalendar;
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
                Text = Strings.T("stats.range.custom"),
                FontSize = 12,
                FontWeight = isActive ? FontWeights.Medium : FontWeights.Normal,
                Foreground = isActive ? accent : textSecondary,
            },
        };
        border.MouseLeftButtonUp += (s, e) => ShowDatePickerPopup(border, accent);
        return border;
    }

    /// <summary>
    /// A Calendar popup anchored under the "Otra fecha" tab, with every day
    /// that has no recorded usage history blacked out — per the request,
    /// only days we actually have data for should be pickable at all,
    /// rather than letting the user land on an empty chart. Picking a day
    /// switches straight to Custom range and closes the popup.
    /// </summary>
    private void ShowDatePickerPopup(FrameworkElement anchor, Brush accent)
    {
        var daysWithData = _historyStore.GetDaysWithData();
        if (daysWithData.Count == 0) return;

        var minDay = daysWithData.Min();
        var maxDay = daysWithData.Max();

        var calendar = new System.Windows.Controls.Calendar
        {
            SelectionMode = CalendarSelectionMode.SingleDate,
            DisplayDateStart = minDay.ToDateTime(TimeOnly.MinValue),
            DisplayDateEnd = maxDay.ToDateTime(TimeOnly.MinValue),
            DisplayDate = (_customDate?.Date ?? maxDay.ToDateTime(TimeOnly.MinValue)),
        };
        if (_customDate is { } selected)
        {
            calendar.SelectedDate = selected.Date;
        }

        // BlackoutDates only takes ranges, so the "allowed" sparse set has
        // to be inverted into the gaps between/around it — every stretch of
        // consecutive days with no data, across the whole displayed span.
        var cursor = minDay;
        while (cursor <= maxDay)
        {
            if (!daysWithData.Contains(cursor))
            {
                var gapStart = cursor;
                while (cursor <= maxDay && !daysWithData.Contains(cursor)) cursor = cursor.AddDays(1);
                var gapEnd = cursor.AddDays(-1);
                calendar.BlackoutDates.Add(new CalendarDateRange(gapStart.ToDateTime(TimeOnly.MinValue), gapEnd.ToDateTime(TimeOnly.MinValue)));
            }
            else
            {
                cursor = cursor.AddDays(1);
            }
        }

        // Same hand-picked panel background Render() uses for RootGrid —
        // not a MaterialDesignPaper resource lookup, for the same reason
        // Render() itself avoids one: it reads subtly different from this
        // window's own chosen background and would look like a mismatched
        // popup bolted onto the side.
        var isDark = new PaletteHelper().GetTheme().GetBaseTheme() == BaseTheme.Dark;
        var popupBackground = isDark
            ? new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2E))
            : new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));

        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = popupBackground,
                BorderBrush = accent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.3 },
                Child = calendar,
            },
        };

        calendar.SelectedDatesChanged += (s, e) =>
        {
            if (calendar.SelectedDate is { } picked)
            {
                _customDate = new DateTimeOffset(picked, DateTimeOffset.Now.Offset);
                _range = StatsRange.Custom;
                _showCalendar = false;
                popup.IsOpen = false;
                Render();
            }
        };

        _datePickerPopup = popup;
        popup.IsOpen = true;
    }

    private FrameworkElement BuildRangeTab(string text, StatsRange range, Brush textSecondary, Brush accent)
    {
        var isActive = _range == range && !_showCalendar;
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
            if (_range == range && !_showCalendar) return;
            _range = range;
            _showCalendar = false;
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

    /// <summary>Called by TrayOrchestrator right after it applies a new theme/accent, so an already-open Stats window doesn't keep showing stale colors until the user happens to click something that triggers a Render().</summary>
    public void RefreshTheme() => Render();

    /// <summary>Called by TrayOrchestrator once the periodic transcript scan refreshes _promptScanCache, so an open calendar view picks up newly-scanned days without needing a click.</summary>
    public void OnPromptScanRefreshed()
    {
        if (_showCalendar) Render();
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

        // Its anchor (a tab border) is about to be torn down and rebuilt
        // below — an open popup pointed at the old, now-orphaned instance
        // would be left dangling in a weird spot.
        if (_datePickerPopup is not null) _datePickerPopup.IsOpen = false;

        BuildRangeSelector(textPrimary, textSecondary, accent);
        BuildPromptModeSelector(textSecondary, promptLineBrush);
        // Nuevos/Totales only means something for the line chart — nothing
        // to toggle while the calendar view is showing instead.
        PromptModeSelectorHost.Visibility = _showCalendar ? Visibility.Collapsed : Visibility.Visible;

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

        if (_showCalendar)
        {
            ContentHost.Children.Add(BuildCalendarView(textPrimary, textSecondary, accent, gridBrush));
            ResizeToFitCalendar();
            return;
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

        // Even after measuring the real chrome overhead above, WPF's own
        // ScrollViewer/DPI rounding leaves a small but real gap between what
        // this math predicts and what the ScrollViewer actually decides it
        // needs — enough, in practice, to still trigger a scrollbar. Rather
        // than chase that gap pixel-for-pixel, budget deliberately less
        // than the full measured space so there's real headroom to absorb
        // it, instead of racing WPF for the exact last pixel.
        const double HeightSafetyMargin = 40;
        var availableForCharts = viewportHeight - ContentHost.Margin.Top - ContentHost.Margin.Bottom - dashboardHeight - chromeOverhead - HeightSafetyMargin;
        var chartHeight = Math.Max(MinChartHeight, availableForCharts / _serviceNames.Count);

        var blocks = ChartBuilder.BuildServiceBlocks(_serviceNames, _historyStore, since, until,
            chartWidth, chartHeight, textPrimary, textSecondary, accent, fillBrush, gridBrush,
            viewportWidth, OnChartZoom, _promptCountStore, promptLineBrush, _promptMode == PromptDisplayMode.Total);
        ContentHost.Children.Add(blocks);
    }

    /// <summary>
    /// A normal month grid (weekday header + up to 6 rows of days), each
    /// cell showing that day's combined Claude Code + Codex prompt total
    /// and shaded by it relative to the busiest day in the SAME month —
    /// relative rather than to some fixed scale, so the shading stays
    /// meaningful as more history accumulates instead of everything
    /// reading as pale until some arbitrary global max is hit. Pulls
    /// straight from _promptScanCache (see that class) — no disk access
    /// here, just filtering timestamps already in memory, however often
    /// month navigation re-renders this.
    /// </summary>
    private FrameworkElement BuildCalendarView(Brush textPrimary, Brush textSecondary, Brush accent, Brush gridBrush)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

        var (minData, _) = GetPromptDataBounds();
        var thisMonthStart = new DateTimeOffset(DateTime.Now.Year, DateTime.Now.Month, 1, 0, 0, 0, DateTimeOffset.Now.Offset);
        var canGoPrev = minData is { } minAt && minAt < _calendarMonth;
        var canGoNext = _calendarMonth.AddMonths(1) <= thisMonthStart;

        var header = new Grid { Margin = new Thickness(4, 0, 4, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var prevButton = BuildCalendarNavButton("‹", canGoPrev, textPrimary, textSecondary);
        if (canGoPrev) prevButton.MouseLeftButtonUp += (s, e) => { _calendarMonth = _calendarMonth.AddMonths(-1); Render(); };
        Grid.SetColumn(prevButton, 0);
        header.Children.Add(prevButton);

        var monthLabel = new TextBlock
        {
            Text = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(_calendarMonth.ToString("MMMM yyyy", CultureInfo.CurrentCulture)),
            FontSize = 14,
            FontWeight = FontWeights.Medium,
            Foreground = textPrimary,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(monthLabel, 1);
        header.Children.Add(monthLabel);

        var nextButton = BuildCalendarNavButton("›", canGoNext, textPrimary, textSecondary);
        if (canGoNext) nextButton.MouseLeftButtonUp += (s, e) => { _calendarMonth = _calendarMonth.AddMonths(1); Render(); };
        Grid.SetColumn(nextButton, 2);
        header.Children.Add(nextButton);

        panel.Children.Add(header);

        // Weekday header — starts on whatever day the current culture
        // considers the start of the week (Monday for es-ES) rather than
        // hardcoding Sunday-first.
        var firstDow = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var weekdayRow = new Grid();
        for (var i = 0; i < 7; i++) weekdayRow.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 7; i++)
        {
            var dow = (DayOfWeek)(((int)firstDow + i) % 7);
            var label = new TextBlock
            {
                Text = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames[(int)dow].ToUpper(CultureInfo.CurrentCulture),
                FontSize = 10,
                FontWeight = FontWeights.Medium,
                Foreground = textSecondary,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6),
            };
            Grid.SetColumn(label, i);
            weekdayRow.Children.Add(label);
        }
        panel.Children.Add(weekdayRow);

        var dailyCountsByAgent = GetDailyPromptCountsByAgent(_calendarMonth);
        var dailyCounts = dailyCountsByAgent.ToDictionary(kv => kv.Key, kv => kv.Value.Values.Sum());
        var maxCount = dailyCounts.Count > 0 ? dailyCounts.Values.Max() : 0;

        var daysInMonth = DateTime.DaysInMonth(_calendarMonth.Year, _calendarMonth.Month);
        var firstOfMonth = new DateTime(_calendarMonth.Year, _calendarMonth.Month, 1);
        var leadingBlanks = (((int)firstOfMonth.DayOfWeek - (int)firstDow) + 7) % 7;
        var totalCells = leadingBlanks + daysInMonth;
        var rows = (int)Math.Ceiling(totalCells / 7.0);
        var today = DateOnly.FromDateTime(DateTime.Now);

        var grid = new Grid();
        for (var c = 0; c < 7; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var cellIndex = 0; cellIndex < totalCells; cellIndex++)
        {
            var dayNum = cellIndex - leadingBlanks + 1;
            if (dayNum < 1 || dayNum > daysInMonth) continue;

            var date = DateOnly.FromDateTime(new DateTime(_calendarMonth.Year, _calendarMonth.Month, dayNum));
            var count = dailyCounts.GetValueOrDefault(date);
            var isToday = date == today;

            // A floor on the low end so any day with at least one prompt
            // is visibly tinted rather than nearly-transparent next to a
            // single outlier day dominating the scale.
            var intensity = maxCount > 0 ? count / (double)maxCount : 0;
            var cellBg = accent.Clone();
            cellBg.Opacity = count > 0 ? Math.Clamp(0.14 + intensity * 0.66, 0.14, 0.8) : 0.0;

            var dayNumberText = new TextBlock
            {
                Text = dayNum.ToString(),
                FontSize = 11,
                Foreground = textSecondary,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var countText = new TextBlock
            {
                Text = count > 0 ? count.ToString() : "–",
                FontSize = 14,
                FontWeight = count > 0 ? FontWeights.Bold : FontWeights.Normal,
                Foreground = count > 0 ? textPrimary : textSecondary,
                Opacity = count > 0 ? 1.0 : 0.4,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0),
            };
            var cellContent = new StackPanel();
            cellContent.Children.Add(dayNumberText);
            cellContent.Children.Add(countText);

            var cell = new Border
            {
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(2),
                Padding = new Thickness(4, 6, 4, 6),
                MinHeight = 54,
                Background = count > 0 ? cellBg : Brushes.Transparent,
                BorderBrush = isToday ? accent : gridBrush,
                BorderThickness = new Thickness(isToday ? 1.5 : 1),
                Child = cellContent,
            };
            if (count > 0 && dailyCountsByAgent.TryGetValue(date, out var byAgent))
            {
                cell.ToolTip = string.Join("\n", DashboardAgents
                    .Select(agent => $"{AgentDisplayNames.For(agent)}: {Strings.F("stats.dashboard.prompts", byAgent.GetValueOrDefault(agent))}"));
            }
            Grid.SetRow(cell, cellIndex / 7);
            Grid.SetColumn(cell, cellIndex % 7);
            grid.Children.Add(cell);
        }

        panel.Children.Add(grid);
        return panel;
    }

    private static Border BuildCalendarNavButton(string glyph, bool enabled, Brush textPrimary, Brush textSecondary)
    {
        return new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = enabled ? Cursors.Hand : Cursors.Arrow,
            Child = new TextBlock
            {
                Text = glyph,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = enabled ? textPrimary : textSecondary,
                Opacity = enabled ? 1.0 : 0.35,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    /// <summary>Prompt count per local calendar day, broken down by agent, for days that fall within the given month — the combined total is just the per-agent values summed.</summary>
    private Dictionary<DateOnly, Dictionary<string, int>> GetDailyPromptCountsByAgent(DateTimeOffset monthStart)
    {
        var monthEnd = monthStart.AddMonths(1);
        var result = new Dictionary<DateOnly, Dictionary<string, int>>();
        foreach (var agent in DashboardAgents)
        {
            foreach (var at in _promptScanCache.Get(agent).Timestamps)
            {
                var local = at.ToLocalTime();
                if (local < monthStart || local >= monthEnd) continue;
                var day = DateOnly.FromDateTime(local.Date);
                if (!result.TryGetValue(day, out var byAgent))
                {
                    byAgent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    result[day] = byAgent;
                }
                byAgent[agent] = byAgent.GetValueOrDefault(agent) + 1;
            }
        }
        return result;
    }

    /// <summary>Earliest/latest prompt timestamp across both agents, for deciding whether month-navigation arrows have anywhere to go.</summary>
    private (DateTimeOffset? Min, DateTimeOffset? Max) GetPromptDataBounds()
    {
        DateTimeOffset? min = null, max = null;
        foreach (var agent in DashboardAgents)
        {
            foreach (var at in _promptScanCache.Get(agent).Timestamps)
            {
                var local = at.ToLocalTime();
                if (min is null || local < min) min = local;
                if (max is null || local > max) max = local;
            }
        }
        return (min, max);
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

    // Both read PromptScanCache — an in-memory filter over the last
    // background scan, refreshed on TrayOrchestrator's 60-minute timer —
    // rather than rescanning every transcript file live. That used to
    // happen on every Render() (window open, every range-tab click) and
    // was pegging the CPU; a full-transcript scan is fine once an hour in
    // the background, not fine on every UI interaction.
    private int GetAgentPromptCountInRange(string agent, DateTimeOffset since, DateTimeOffset? until) =>
        _promptScanCache.PromptCountInRange(agent, since, until);

    private int GetAgentTotalPromptCount(string agent) => _promptScanCache.TotalPromptCount(agent);

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
        // Custom shows the actual picked day here (e.g. "3 ago") instead of
        // the literal "Otra fecha" tab text — that's what's actually useful
        // to know once you're looking at a specific day's numbers.
        var rangeLabel = _range == StatsRange.Custom && _customDate is { } customDay
            ? customDay.ToString("d MMM")
            : Strings.T(_range switch
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
            var rangePromptCount = GetAgentPromptCountInRange(agent, since, until);
            // Always shown, even all-zero — a card that silently disappears
            // instead of reading "0" is more confusing than reassuring: it
            // leaves you wondering whether the agent just isn't tracked at
            // all, rather than confirming it genuinely had no activity.
            wrap.Children.Add(BuildDashboardCard(agent, rangeLabel, rangePromptCount, rangeProjectCount, rangeTaskCount, textPrimary, textSecondary, cardBackground));
        }

        foreach (var agent in DashboardAgents)
        {
            var tasks = tasksByAgent[agent];
            var totalProjectCount = tasks.Select(t => t.ProjectPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var totalTaskCount = tasks.Count;
            // A live full-transcript scan, not the latest PromptCountStore
            // snapshot — always accurate, never stuck at a value read
            // while some transcript file happened to be unreadable.
            var totalPromptCount = GetAgentTotalPromptCount(agent);
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
