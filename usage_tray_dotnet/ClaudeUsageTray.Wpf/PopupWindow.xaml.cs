using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MaterialDesignThemes.Wpf;

namespace ClaudeUsageTray;

public partial class PopupWindow : Window
{
    private const double SingleColumnWidth = 288;
    private const string IconRefresh = "\uE72C";
    private const string IconSettings = "\uE713";

    public event EventHandler? RefreshRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? StatsRequested;

    private readonly List<(Border Fill, double TargetWidth)> _animatedBars = new();
    /// <summary>
    /// Last-seen percent per bar (keyed by "Service|Label"), so a Render()
    /// call that repeats an already-shown value — RefreshAllAsync re-renders
    /// as each provider's data streams in, so an earlier provider's
    /// already-displayed bars get rebuilt again on every later provider's
    /// arrival — sets the bar directly instead of replaying its fill
    /// animation from zero a second (or third) time.
    /// </summary>
    private readonly Dictionary<string, int> _lastBarPercents = new();
    private double _barWidth = SingleColumnWidth;
    private Brush _textPrimary = Brushes.Black;
    private Brush _textSecondary = Brushes.Gray;
    private TextBlock? _refreshGlyph;
    private RotateTransform? _refreshSpin;
    private Button? _refreshButton;
    private bool _isRefreshing;

    /// <summary>Null = the default percent-based green/amber/red bars. A hex string = every bar uses that one flat color regardless of percent.</summary>
    public string? FlatBarColorHex { get; set; }
    public bool AnimationsEnabled { get; set; } = true;

    // The lowest the window's BOTTOM edge is ever allowed to sit — set once
    // when the panel first appears (Top + Height at that moment). As more
    // services' data streams in and the content grows taller, the window
    // must grow upward from this fixed bottom, never push further down the
    // screen. This has to be the bottom, not just the initial Top: the
    // first open is usually short (one provider's data, or the spinner),
    // so its Top alone is nowhere near where a much taller panel's bottom
    // should anchor — using Top directly here made the panel jump much
    // higher than the actual content growth warranted.
    private double _maxBottom;

    public PopupWindow()
    {
        InitializeComponent();
    }

    public void Render(IEnumerable<UsageSnapshot> snapshots, bool hasAnyEnabled, DateTime? lastUpdated)
    {
        ApplyThemeColors();

        ContentHost.Children.Clear();
        _animatedBars.Clear();

        var list = snapshots.ToList();
        _barWidth = SingleColumnWidth;
        var contentWidth = SingleColumnWidth;

        if (!hasAnyEnabled)
        {
            var msg = BuildEmptyMessage(Strings.T("popup.noservices"));
            msg.Width = contentWidth;
            ContentHost.Children.Add(msg);
        }
        else if (list.Count == 0)
        {
            var spinner = BuildSpinner();
            spinner.Width = contentWidth;
            ContentHost.Children.Add(spinner);
        }
        else
        {
            var column = new StackPanel { Width = contentWidth };
            foreach (var snap in list)
            {
                column.Children.Add(BuildServiceBlock(snap));
            }
            ContentHost.Children.Add(column);
        }

        var footer = BuildFooter(lastUpdated);
        footer.Width = contentWidth;
        ContentHost.Children.Add(footer);

        // Play right here, not just from ShowNearCursor: RefreshAllAsync
        // calls Render() again on an already-open popup as fresh data
        // arrives, which was rebuilding every bar at width 0 with nothing
        // to ever animate it back up — bars just went blank on refresh.
        // Triggering the animation on every Render() fixes both the first
        // open and any live update while the panel stays open.
        PlayBarAnimations();
        if (_isRefreshing) SetRefreshing(true);

        // A live re-render while the panel is already open (a new provider's
        // data landing) can change its height. Resize/reposition smoothly
        // instead of letting SizeToContent silently grow the window
        // downward off-screen — see ShowNearCursor and AnimateToNewSize.
        if (IsVisible) AnimateToNewSize();
    }

    /// <summary>Swaps the refresh icon for a spinning one and disables the button while a fetch is in flight.</summary>
    public void SetRefreshing(bool refreshing)
    {
        _isRefreshing = refreshing;
        if (_refreshButton is null || _refreshGlyph is null) return;

        _refreshButton.IsEnabled = !refreshing;
        if (refreshing)
        {
            _refreshSpin ??= new RotateTransform();
            _refreshGlyph.RenderTransformOrigin = new Point(0.5, 0.5);
            _refreshGlyph.RenderTransform = _refreshSpin;
            var spin = new DoubleAnimation { From = 0, To = 360, Duration = TimeSpan.FromSeconds(0.8), RepeatBehavior = RepeatBehavior.Forever };
            _refreshSpin.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
        else
        {
            _refreshSpin?.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    /// <summary>
    /// Resolves explicit near-white/near-black colors from the currently
    /// active theme instead of trusting MaterialDesignPaper/BodyLight —
    /// those read as too dull/low-contrast for this panel specifically.
    /// </summary>
    private void ApplyThemeColors()
    {
        var isDark = new PaletteHelper().GetTheme().GetBaseTheme() == BaseTheme.Dark;

        RootBorder.Background = isDark
            ? new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2E))
            : new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));

        _textPrimary = isDark
            ? new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2))
            : new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));

        _textSecondary = isDark
            ? new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xB8))
            : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

        UpdatePinGlyphColor();
    }

    private void UpdatePinGlyphColor()
    {
        // "Glyph" is named inside PinButton's ControlTemplate, so it lives
        // in the template's own name scope — not reachable as a field, only
        // via FindName on the applied template.
        PinButton.ApplyTemplate();
        if (PinButton.Template.FindName("Glyph", PinButton) is not TextBlock glyph) return;
        glyph.Foreground = PinButton.IsChecked == true ? _textPrimary : _textSecondary;
        glyph.Opacity = PinButton.IsChecked == true ? 1.0 : 0.45;
    }

    /// <summary>Whether the panel should stay open regardless of cursor position — see TrayOrchestrator's away-hide poll.</summary>
    public bool IsPinned => PinButton.IsChecked == true;

    private void OnPinToggled(object sender, RoutedEventArgs e) => UpdatePinGlyphColor();

    private void OnStatsClick(object sender, RoutedEventArgs e) => StatsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// WindowStyle="None" means there's no native title bar to drag by, so
    /// any empty area of the panel itself doubles as one. Buttons inside
    /// (refresh, settings, pin, links) mark their own mouse-down handled,
    /// so this never fights with actually clicking them.
    /// </summary>
    private void OnRootMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try
        {
            DragMove();
            // DragMove() blocks until the drag ends — the panel's bottom
            // edge just moved, so the anchor AnimateToNewSize resizes
            // against needs to move with it, or the next live data update
            // would snap the panel back toward the old position.
            _maxBottom = Top + ActualHeight;
        }
        catch (InvalidOperationException)
        {
            // DragMove() throws if called outside a genuine mouse-down
            // gesture (e.g. a stray call while the button's already up) —
            // harmless, just skip the drag.
        }
    }

    private FrameworkElement BuildServiceBlock(UsageSnapshot snap)
    {
        var block = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };

        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = ServiceIcons.Build(snap.ServiceName, 18, _textPrimary);
        icon.Margin = new Thickness(0, 0, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(icon, 0);

        var name = new TextBlock { Text = snap.ServiceName, FontSize = 14, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center, Foreground = _textPrimary };
        Grid.SetColumn(name, 1);

        header.Children.Add(icon);
        header.Children.Add(name);
        block.Children.Add(header);

        if (!snap.Ok)
        {
            var err = new TextBlock
            {
                Text = snap.ErrorMessage ?? Strings.T("popup.error.generic"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
            };
            err.SetResourceReference(TextBlock.ForegroundProperty, "MaterialDesignValidationErrorBrush");
            block.Children.Add(err);
            return block;
        }

        foreach (var bar in snap.Bars)
        {
            block.Children.Add(BuildBarRow(snap.ServiceName, bar));
        }

        if (!string.IsNullOrEmpty(snap.ExtraLine))
        {
            var extra = new TextBlock { Text = snap.ExtraLine, FontSize = 12, Margin = new Thickness(0, 4, 0, 0), Foreground = _textSecondary };
            block.Children.Add(extra);
        }

        return block;
    }

    private FrameworkElement BuildBarRow(string serviceName, UsageBar bar)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

        var labelRow = new Grid();
        labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock { Text = bar.Label, FontSize = 12, Foreground = _textSecondary };
        Grid.SetColumn(label, 0);

        var pct = new TextBlock { Text = $"{bar.Percent}%", FontSize = 12, FontWeight = FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Right, Foreground = _textPrimary };
        Grid.SetColumn(pct, 1);

        labelRow.Children.Add(label);
        labelRow.Children.Add(pct);
        stack.Children.Add(labelRow);

        stack.Children.Add(BuildProgressBar($"{serviceName}|{bar.Label}", bar.Percent));

        if (bar.ResetAt is { } resetAt)
        {
            // Short windows (Claude's rolling 5-hour limit, say) get a
            // precise countdown; anything a day or more out keeps the
            // calendar-style "el 5 de agosto (en 4 días)" phrasing.
            var resetText = resetAt - DateTimeOffset.Now < TimeSpan.FromDays(1)
                ? TimeFormat.ResetCountdown(resetAt)
                : TimeFormat.ResetLine(resetAt);
            var reset = new TextBlock { Text = Strings.F("popup.resets", resetText), FontSize = 11, Margin = new Thickness(0, 5, 0, 0), Foreground = _textSecondary };
            stack.Children.Add(reset);
        }

        return stack;
    }

    private Grid BuildProgressBar(string barKey, int percent)
    {
        const double height = 9;
        var grid = new Grid { Height = height, Margin = new Thickness(0, 6, 0, 0) };

        var track = new Border
        {
            CornerRadius = new CornerRadius(height / 2),
            Background = (Brush)new BrushConverter().ConvertFrom("#26808080")!,
        };

        var fill = new Border
        {
            CornerRadius = new CornerRadius(height / 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0,
            Background = FlatBarColorHex is { } flatHex ? FlatGradient(flatHex) : GradientForPercent(percent),
        };

        grid.Children.Add(track);
        grid.Children.Add(fill);

        var targetWidth = Math.Clamp(percent, 0, 100) / 100.0 * _barWidth;
        var alreadyShownAtThisValue = _lastBarPercents.TryGetValue(barKey, out var prevPercent) && prevPercent == percent;
        _lastBarPercents[barKey] = percent;

        if (alreadyShownAtThisValue)
        {
            fill.Width = targetWidth;
        }
        else
        {
            _animatedBars.Add((fill, targetWidth));
        }

        return grid;
    }

    private FrameworkElement BuildEmptyMessage(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 12, 0, 12),
            Foreground = _textSecondary,
        };
    }

    private FrameworkElement BuildSpinner()
    {
        var glyph = new TextBlock
        {
            Text = IconRefresh,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Foreground = _textSecondary,
        };
        var rotate = new RotateTransform();
        glyph.RenderTransform = rotate;

        var host = new Border { Margin = new Thickness(0, 20, 0, 20), Child = glyph };

        var spin = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(1.1),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        rotate.BeginAnimation(RotateTransform.AngleProperty, spin);

        return host;
    }

    private FrameworkElement BuildFooter(DateTime? lastUpdated)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var updated = new TextBlock
        {
            Text = lastUpdated is { } t ? Strings.F("popup.updated", TimeFormat.Ago(t)) : "",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _textSecondary,
        };
        Grid.SetColumn(updated, 0);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var (refreshButton, refreshGlyph) = BuildIconButtonWithGlyph(IconRefresh, Strings.T("popup.tooltip.refresh"), () => RefreshRequested?.Invoke(this, EventArgs.Empty));
        _refreshButton = refreshButton;
        _refreshGlyph = refreshGlyph;
        buttons.Children.Add(refreshButton);
        buttons.Children.Add(BuildIconButton(IconSettings, Strings.T("popup.tooltip.settings"), () => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        Grid.SetColumn(buttons, 1);

        row.Children.Add(updated);
        row.Children.Add(buttons);
        return row;
    }

    private Button BuildIconButton(string glyph, string tooltip, Action onClick) => BuildIconButtonWithGlyph(glyph, tooltip, onClick).Button;

    private (Button Button, TextBlock Glyph) BuildIconButtonWithGlyph(string glyph, string tooltip, Action onClick)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _textSecondary,
        };
        var button = new Button
        {
            Content = icon,
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = tooltip,
        };
        button.SetResourceReference(StyleProperty, "MaterialDesignFlatButton");
        button.Click += (s, e) => onClick();
        return (button, icon);
    }

    private static Brush GradientForPercent(int percent)
    {
        var (from, to) = percent switch
        {
            < 60 => ("#72D08F", "#3F9E63"),
            < 85 => ("#F3C36A", "#D99420"),
            _ => ("#EE8484", "#CE3D3D"),
        };
        return new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(from),
            (Color)ColorConverter.ConvertFromString(to),
            new Point(0, 0), new Point(1, 0));
    }

    /// <summary>Same lighter-to-base gradient treatment as the percent-based bars, but built from a single chosen accent color instead.</summary>
    private static Brush FlatGradient(string hex)
    {
        var baseColor = (Color)ColorConverter.ConvertFromString(hex);
        var lighter = Color.FromRgb(
            (byte)Math.Min(255, baseColor.R + 40),
            (byte)Math.Min(255, baseColor.G + 40),
            (byte)Math.Min(255, baseColor.B + 40));
        return new LinearGradientBrush(lighter, baseColor, new Point(0, 0), new Point(1, 0));
    }

    public void ShowNearCursor()
    {
        // Every fresh open starts unpinned — this only runs when the panel
        // wasn't already visible (see the IsVisible early-return in
        // TrayOrchestrator's poll loop), so it never disturbs an
        // already-open pinned panel.
        PinButton.IsChecked = false;

        // Measure against the real content (not ActualWidth/Height, which
        // are stale/zero the first time — before the window has ever been
        // shown, SizeToContent hasn't resolved a real size yet). Using
        // ActualHeight here was why the very first popup could render
        // mostly below the screen edge, with only its top sliver visible.
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = DesiredSize;

        var cursor = NativeScreenHelper.GetCursorPosition();
        var screen = NativeScreenHelper.GetWorkAreaForPoint(cursor);
        var dpi = VisualTreeHelper.GetDpi(this);

        var widthDip = desired.Width;
        var heightDip = desired.Height;
        var cursorXDip = cursor.X / dpi.DpiScaleX;
        var screenRightDip = screen.Right / dpi.DpiScaleX;
        var screenLeftDip = screen.Left / dpi.DpiScaleX;
        var screenTopDip = screen.Top / dpi.DpiScaleY;
        var screenBottomDip = screen.Bottom / dpi.DpiScaleY;

        // A prior AnimateToNewSize() call (from the last time this reused
        // window was open) may have left an active/held animation on these
        // properties — WPF keeps an animated value in control even after
        // the animation finishes (FillBehavior.HoldEnd), so a plain
        // assignment below would silently be ignored without this. That's
        // what caused the popup to reopen wherever it last animated to
        // instead of freshly next to the cursor.
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        BeginAnimation(TopProperty, null);

        Width = widthDip;
        Height = heightDip;
        Left = Math.Max(screenLeftDip + 8, Math.Min(cursorXDip, screenRightDip - widthDip - 8));
        Top = Math.Max(screenTopDip + 8, screenBottomDip - heightDip - 8);

        // The bottom edge right now is the lowest the window will ever be
        // allowed to reach — later content growth (more services' data
        // streaming in) only ever pushes the top edge up from here.
        _maxBottom = Top + Height;

        Show();
        Activate();

        // Belt-and-suspenders: clamp again against the real rendered size,
        // in case actual layout (fonts, DPI rounding) differs slightly from
        // the pre-show measure pass. This guarantees the window can never
        // end up partly off-screen.
        if (ActualWidth > 0 && ActualHeight > 0)
        {
            Left = Math.Max(screenLeftDip + 8, Math.Min(Left, screenRightDip - ActualWidth - 8));
            Top = Math.Max(screenTopDip + 8, Math.Min(Top, screenBottomDip - ActualHeight - 8));
            _maxBottom = Top + ActualHeight;
        }
    }

    /// <summary>
    /// Smoothly resizes/repositions after a live content change (e.g. a
    /// second provider's data landing while the panel is already open).
    /// The top edge only ever moves up from wherever it currently is —
    /// the bottom stays anchored at `_maxBottom`, however tall the new
    /// content is.
    /// </summary>
    private void AnimateToNewSize()
    {
        // Measure() on a Window that's already shown and already has a
        // resolved layout can return a stale/degenerate DesiredSize (this
        // produced an ~2x2 result once, which then made the away-hover
        // hit-test think the whole panel was a single pixel and closed it
        // on the very next poll). InvalidateMeasure() first forces WPF to
        // actually recompute against the real current content instead of
        // handing back a cached answer.
        var content = (FrameworkElement)Content;
        content.InvalidateMeasure();
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = DesiredSize;

        // Defensive floor: never animate to an obviously-wrong tiny size.
        if (desired.Width < 100 || desired.Height < 50) return;

        var newTop = Math.Min(Top, _maxBottom - desired.Height);

        var duration = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(WidthProperty, new DoubleAnimation { To = desired.Width, Duration = duration, EasingFunction = ease });
        BeginAnimation(HeightProperty, new DoubleAnimation { To = desired.Height, Duration = duration, EasingFunction = ease });
        BeginAnimation(TopProperty, new DoubleAnimation { To = newTop, Duration = duration, EasingFunction = ease });
    }

    private void PlayBarAnimations()
    {
        for (var i = 0; i < _animatedBars.Count; i++)
        {
            var (fill, targetWidth) = _animatedBars[i];

            if (!AnimationsEnabled)
            {
                fill.Width = targetWidth;
                continue;
            }

            var anim = new DoubleAnimation
            {
                From = 0,
                To = targetWidth,
                Duration = TimeSpan.FromMilliseconds(500),
                BeginTime = TimeSpan.FromMilliseconds(80 * i),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            fill.BeginAnimation(WidthProperty, anim);
        }
    }
}
