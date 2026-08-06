using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
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
    public event EventHandler? CloseRequested;

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

    // Same 18px-native-draw approach as SettingsWindow/AppDialogWindow's
    // caption icon — built once and reused for the loading state's small
    // app-identity header, since there's no window chrome here to hang a
    // real WM_SETICON icon off of.
    private static readonly Lazy<BitmapSource> AppIconSource = new(() =>
    {
        using var icon = IconFactory.BuildRobotIcon(18);
        return Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
    });

    /// <summary>Null = the default percent-based green/amber/red bars. A hex string = every bar uses that one flat color regardless of percent.</summary>
    public string? FlatBarColorHex { get; set; }
    public bool AnimationsEnabled { get; set; } = true;
    public PopupWindowStyle StyleMode { get; set; } = PopupWindowStyle.Standard;
    public int OpacityPercent { get; set; } = 100;
    public int BlurPercent { get; set; } = 45;

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

    // Captured once so Blur mode can strip the shadow (see ApplyThemeColors)
    // and Standard mode can put the exact same instance back, rather than
    // reconstructing a DropShadowEffect with hand-copied parameters.
    private readonly Effect? _standardShadowEffect;

    // Set on every Render() so the NEXT one can tell whether StyleMode
    // itself just changed — see Render()'s styleChanged branch.
    private PopupWindowStyle? _lastRenderedStyleMode;

    public PopupWindow()
    {
        InitializeComponent();
        _standardShadowEffect = RootBorder.Effect;
    }

    /// <summary>
    /// Forces one real Show()+Loaded layout pass, off-screen, right after
    /// construction — the same root cause as this session's invisible
    /// 100%-toast bug: a manual Measure() against a Window that has never
    /// actually been shown (as ShowNearCursor does, to size itself before
    /// its first real appearance) is unreliable until the window's visual
    /// tree and DynamicResource-bound templates have gone through one
    /// genuine layout pass. Warming that up here, while nobody's looking,
    /// means the user's actual first hover/click is no longer that fresh,
    /// untested first pass — which is what made it take several tries.
    /// </summary>
    public void WarmUp()
    {
        Left = -32000;
        Top = -32000;
        Show();
        Loaded += OnWarmUpLoaded;
    }

    private void OnWarmUpLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnWarmUpLoaded;
        Hide();
    }

    public void Render(IEnumerable<UsageSnapshot> snapshots, bool hasAnyEnabled, DateTime? lastUpdated, int totalEnabled = 0)
    {
        // Standard vs Blur changes the window's real layout size by a hard
        // 36px in each dimension (the shadow margin appearing/disappearing
        // — see ApplyThemeColors) the instant this method's ApplyThemeColors
        // call below runs. That's a fundamentally different kind of size
        // change from AnimateToNewSize's actual purpose (content growing
        // as more services' data streams in) — animating THIS one left the
        // window ~220ms into every mode switch with the new margin/shadow/
        // DWM-blur state already applied but the old Width/Height/Top not
        // yet caught up, which is what read as a doubled-up border or a
        // corner getting cut off mid-resize. See ResizeInstant.
        var styleChanged = _lastRenderedStyleMode is { } lastStyle && lastStyle != StyleMode;
        _lastRenderedStyleMode = StyleMode;

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
            // Nothing has come back at all yet — there's no service block
            // on screen to give the panel any context, so this is the one
            // state that needs its own "yes, this is [app name] and it's
            // working on it" identity, not just a bare spinner.
            var loading = BuildLoadingState(contentWidth);
            ContentHost.Children.Add(loading);
        }
        else
        {
            var column = new StackPanel { Width = contentWidth };
            if (list.Count < totalEnabled)
            {
                // Some services already have real data — from here on a
                // real ready/total progress bar is more honest than a
                // generic spinner, since we now actually know how far along
                // the batch is.
                column.Children.Add(BuildLoadingMoreIndicator(contentWidth, list.Count, totalEnabled));
            }
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
        if (IsVisible)
        {
            if (styleChanged) ResizeInstant();
            else AnimateToNewSize();
        }
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

        var baseColor = isDark ? Color.FromRgb(0x2B, 0x2B, 0x2E) : Color.FromRgb(0xFA, 0xFA, 0xFA);
        var hwnd = new WindowInteropHelper(this).Handle;

        if (StyleMode == PopupWindowStyle.Blur)
        {
            // Windows doesn't expose a variable blur radius — the OS-driven
            // blur itself is always the same strength once acrylic is on,
            // so this slider is really a transparency dial for the tint
            // sitting on top of it (see Strings["appearance.blur.label"]).
            // Floor kept above 0 (unlike Standard mode's opacity, which can
            // go there) since the legacy accent-blur API still bakes in its
            // own base tint/noise even at low alpha — but pushed low enough
            // that 100% actually reads as see-through, not just "less
            // opaque"; the text-legibility side of this is now covered
            // separately by the pure-white/black text below, not by this
            // floor.
            var tintAlpha = (byte)Math.Clamp(235 - BlurPercent / 100.0 * 185, 50, 235);
            RootBorder.Background = new SolidColorBrush(Color.FromArgb(tintAlpha, baseColor.R, baseColor.G, baseColor.B));

            // The composition blur applies to the whole HWND rectangle, not
            // just the pixels WPF actually paints — with the 18px shadow
            // margin left in place, that outer ring is blurred-but-untinted
            // (nothing there to tint) and reads as a hard rectangular seam
            // right where the drop shadow's blur radius used to fade out
            // invisibly. Dropping the margin and the shadow itself so the
            // window's real bounds match the rounded card exactly removes
            // that seam; the OS-level rounded corners take over for the
            // rounding the shadow's breathing room used to protect.
            RootGrid.Margin = new Thickness(0);
            RootBorder.Effect = null;
            if (hwnd != IntPtr.Zero) DwmHelper.SetAcrylicBlur(hwnd, baseColor, tintAlpha);
        }
        else
        {
            var alpha = (byte)Math.Clamp(Math.Round(OpacityPercent / 100.0 * 255), 0, 255);
            RootBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
            RootGrid.Margin = new Thickness(18);
            RootBorder.Effect = _standardShadowEffect;
            if (hwnd != IntPtr.Zero) DwmHelper.DisableBlur(hwnd);
        }

        // DWM rounds the corners of the window's real (HWND) bounds, not
        // whatever WPF happens to be painting inside it. In Blur mode those
        // two coincide (margin is 0 above), so rounding helps. In Standard
        // mode the real bounds are 18px bigger on every side — mostly-
        // invisible padding for the drop shadow — and rounding THAT rect
        // showed up as a faint rectangular halo/box around the actual
        // white card. Worse, the preference sticks to the HWND across
        // resizes, so this window (reused between both modes) needs the
        // "don't round" side stated explicitly too, not just skipped —
        // otherwise a Blur-mode session earlier in the same run leaves
        // rounding turned on for every Standard-mode open after it.
        if (hwnd != IntPtr.Zero)
        {
            if (StyleMode == PopupWindowStyle.Blur) DwmHelper.EnableRoundedCorners(hwnd);
            else DwmHelper.DisableRoundedCorners(hwnd);
        }

        // Blur mode sits over arbitrary, unpredictable desktop content (dark
        // photos, busy video) instead of a flat theme-colored fill, so the
        // usual near-white/near-gray pairing loses too much contrast there —
        // pushed to pure white/black for every piece of text, not just the
        // primary line. Standard mode keeps the original softer pairing.
        var blurMode = StyleMode == PopupWindowStyle.Blur;

        _textPrimary = isDark
            ? new SolidColorBrush(blurMode ? Colors.White : Color.FromRgb(0xF2, 0xF2, 0xF2))
            : new SolidColorBrush(blurMode ? Colors.Black : Color.FromRgb(0x1A, 0x1A, 0x1A));

        _textSecondary = isDark
            ? new SolidColorBrush(blurMode ? Colors.White : Color.FromRgb(0xB8, 0xB8, 0xB8))
            : new SolidColorBrush(blurMode ? Colors.Black : Color.FromRgb(0x55, 0x55, 0x55));

        UpdatePinGlyphColor();
        UpdateStatsGlyphColor();
        UpdateCloseGlyphColor();
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

    private void UpdateCloseGlyphColor()
    {
        CloseButton.ApplyTemplate();
        if (CloseButton.Template.FindName("CloseGlyph", CloseButton) is not TextBlock glyph) return;
        glyph.Foreground = _textSecondary;
    }

    private void UpdateStatsGlyphColor()
    {
        StatsButton.ApplyTemplate();
        if (StatsButton.Template.FindName("StatsBar1", StatsButton) is not Rectangle bar1) return;
        if (StatsButton.Template.FindName("StatsBar2", StatsButton) is not Rectangle bar2) return;
        if (StatsButton.Template.FindName("StatsBar3", StatsButton) is not Rectangle bar3) return;
        bar1.Fill = bar2.Fill = bar3.Fill = _textSecondary;
    }

    /// <summary>Whether the panel should stay open regardless of cursor position — see TrayOrchestrator's away-hide poll.</summary>
    public bool IsPinned => PinButton.IsChecked == true;

    /// <summary>Opening Stats pins the main panel too, so both stay up together — the user can still unpin by hand if they don't want that.</summary>
    public void SetPinned(bool pinned) => PinButton.IsChecked = pinned;

    private void OnPinToggled(object sender, RoutedEventArgs e)
    {
        UpdatePinGlyphColor();
        // The close button only makes sense once the panel won't just
        // auto-hide on its own — keep it out of the way (but still holding
        // its layout slot, see PopupWindow.xaml) the rest of the time.
        CloseButton.Visibility = PinButton.IsChecked == true ? Visibility.Visible : Visibility.Hidden;
    }

    private void OnStatsClick(object sender, RoutedEventArgs e) => StatsRequested?.Invoke(this, EventArgs.Empty);

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// WindowStyle="None" means there's no native title bar to drag by, so
    /// any empty area of the panel itself doubles as one. Buttons inside
    /// (refresh, settings, pin, links) mark their own mouse-down handled,
    /// so this never fights with actually clicking them.
    /// </summary>
    private void OnRootMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        // Deliberately repositioning the panel is the same "keep this
        // open" signal as clicking Pin by hand — without this it could
        // vanish mid-drag, or the instant the cursor left it right after.
        PinButton.IsChecked = true;

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
            Background = FlatBarColorHex is { } flatHex ? FlatGradient(flatHex) : ChartBuilder.GradientForPercent(percent),
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

    private FrameworkElement BuildLoadingState(double width)
    {
        var stack = new StackPanel { Width = width, Margin = new Thickness(0, 10, 0, 10) };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16),
        };
        header.Children.Add(new Image { Width = 20, Height = 20, Margin = new Thickness(0, 0, 8, 0), Source = AppIconSource.Value });
        header.Children.Add(new TextBlock
        {
            Text = Strings.T("app.name"),
            FontSize = 14,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _textPrimary,
        });
        stack.Children.Add(header);

        stack.Children.Add(BuildSpinner());

        stack.Children.Add(new TextBlock
        {
            Text = Strings.T("popup.loading"),
            FontSize = 12,
            Foreground = _textSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        });

        return stack;
    }

    /// <summary>
    /// A slim "Actualizando… (X/Y)" strip shown above whichever services
    /// already have data, while the rest are still being fetched — a real
    /// measured fraction, not an indeterminate spinner, since by this point
    /// we genuinely know how many of the enabled services are left.
    /// </summary>
    private FrameworkElement BuildLoadingMoreIndicator(double width, int ready, int total)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };

        stack.Children.Add(new TextBlock
        {
            Text = Strings.F("popup.loading.progress", Strings.T("popup.loading"), ready, total),
            FontSize = 11,
            Foreground = _textSecondary,
            Margin = new Thickness(0, 0, 0, 6),
        });

        const double height = 4;
        var grid = new Grid { Height = height };
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
            Background = (Brush)FindResource("MaterialDesign.Brush.Primary"),
        };
        grid.Children.Add(track);
        grid.Children.Add(fill);
        stack.Children.Add(grid);

        var targetWidth = total > 0 ? width * ready / total : 0;
        if (AnimationsEnabled)
        {
            var anim = new DoubleAnimation
            {
                From = 0,
                To = targetWidth,
                Duration = TimeSpan.FromMilliseconds(400),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            fill.BeginAnimation(WidthProperty, anim);
        }
        else
        {
            fill.Width = targetWidth;
        }

        return stack;
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
        PositionNearCursor();
        Show();
        Activate();
        ReapplyDwmStateAfterShow();
        ReclampToScreen();
    }

    /// <summary>
    /// Sizes and positions the window against the cursor without showing
    /// it — split out of ShowNearCursor so PreviewStyleAsync can do this
    /// same measure-and-place step while the window is still deliberately
    /// hidden (see that method for why).
    /// </summary>
    private void PositionNearCursor()
    {
        // Measure against the real content (not ActualWidth/Height, which
        // are stale/zero the first time — before the window has ever been
        // shown, SizeToContent hasn't resolved a real size yet). Using
        // ActualHeight here was why the very first popup could render
        // mostly below the screen edge, with only its top sliver visible.
        // Goes through the same MeasureRealDesiredSize used by resizes —
        // see its comment — so a mode switch never produces a different
        // real window size for the same content depending on which path
        // (first open vs. resize) happened to trigger it.
        var desired = MeasureRealDesiredSize() ?? new Size(320, 160);

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
    }

    /// <summary>
    /// Belt-and-suspenders: clamp again against the real rendered size once
    /// the window is actually showing, in case real layout (fonts, DPI
    /// rounding) differs slightly from the pre-show measure pass. Shared by
    /// every Show path so the window can never end up partly off-screen.
    /// </summary>
    private void ReclampToScreen()
    {
        if (!(ActualWidth > 0 && ActualHeight > 0)) return;
        var (screenLeftDip, screenRightDip, screenTopDip, screenBottomDip) = GetScreenBoundsDip();
        Left = Math.Max(screenLeftDip + 8, Math.Min(Left, screenRightDip - ActualWidth - 8));
        Top = Math.Max(screenTopDip + 8, Math.Min(Top, screenBottomDip - ActualHeight - 8));
        _maxBottom = Top + ActualHeight;
    }

    /// <summary>
    /// Re-issues the DWM/composition half of ApplyThemeColors right after
    /// Show(), once the window is definitely on screen. Belt-and-suspenders
    /// alongside PreviewStyleAsync's hide-while-settling approach (see that
    /// method) rather than the actual fix for the fade-in itself — but
    /// cheap and idempotent, so there's no reason not to keep it too.
    /// </summary>
    private void ReapplyDwmStateAfterShow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        if (StyleMode == PopupWindowStyle.Blur)
        {
            var isDark = new PaletteHelper().GetTheme().GetBaseTheme() == BaseTheme.Dark;
            var baseColor = isDark ? Color.FromRgb(0x2B, 0x2B, 0x2E) : Color.FromRgb(0xFA, 0xFA, 0xFA);
            var tintAlpha = (byte)Math.Clamp(235 - BlurPercent / 100.0 * 185, 50, 235);
            DwmHelper.SetAcrylicBlur(hwnd, baseColor, tintAlpha);
            DwmHelper.EnableRoundedCorners(hwnd);
        }
        else
        {
            DwmHelper.DisableBlur(hwnd);
            DwmHelper.DisableRoundedCorners(hwnd);
        }
    }

    /// <summary>
    /// Same first-open sizing dance as ShowNearCursor, but anchored to a
    /// given window's bounds instead of the cursor — used by Settings'
    /// Apariencia live preview, where anchoring to the cursor would often
    /// plant the preview panel right on top of (or straddling) the Settings
    /// dialog itself, since the cursor is wherever the user is still
    /// dragging a slider inside it. Prefers opening to the right of the
    /// given window, then the left, and only falls back to an on-screen
    /// clamp (which may overlap) if neither side has room. Doesn't call
    /// Activate() — unlike ShowNearCursor's primary open path, this one has
    /// a modal dialog that should keep keyboard focus throughout.
    /// </summary>
    public void ShowBesideWindow(double ownerLeft, double ownerTop, double ownerWidth, double ownerHeight)
    {
        PinButton.IsChecked = false;
        PositionBesideWindow(ownerLeft, ownerTop, ownerWidth, ownerHeight);
        Show();
        ReapplyDwmStateAfterShow();
        ReclampToScreen();
    }

    /// <summary>Sizes and positions beside the given window without showing it — see PositionNearCursor for why that split exists.</summary>
    private void PositionBesideWindow(double ownerLeft, double ownerTop, double ownerWidth, double ownerHeight)
    {
        var desired = MeasureRealDesiredSize() ?? new Size(320, 160);

        var dpi = VisualTreeHelper.GetDpi(this);
        var ownerCenter = new NativeScreenHelper.POINT
        {
            X = (int)((ownerLeft + ownerWidth / 2) * dpi.DpiScaleX),
            Y = (int)((ownerTop + ownerHeight / 2) * dpi.DpiScaleY),
        };
        var screen = NativeScreenHelper.GetWorkAreaForPoint(ownerCenter);

        var widthDip = desired.Width;
        var heightDip = desired.Height;
        var screenRightDip = screen.Right / dpi.DpiScaleX;
        var screenLeftDip = screen.Left / dpi.DpiScaleX;
        var screenTopDip = screen.Top / dpi.DpiScaleY;
        var screenBottomDip = screen.Bottom / dpi.DpiScaleY;

        double left;
        if (ownerLeft + ownerWidth + 16 + widthDip <= screenRightDip)
            left = ownerLeft + ownerWidth + 16; // to the right of the owner window
        else if (ownerLeft - 16 - widthDip >= screenLeftDip)
            left = ownerLeft - 16 - widthDip; // no room on the right — try the left
        else
            left = Math.Max(screenLeftDip + 8, screenRightDip - widthDip - 8); // neither side fits — clamp on-screen as a last resort

        var top = Math.Max(screenTopDip + 8, Math.Min(ownerTop, screenBottomDip - heightDip - 8));

        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        BeginAnimation(TopProperty, null);

        Width = widthDip;
        Height = heightDip;
        Left = left;
        Top = top;
        _maxBottom = Top + Height;
    }

    // DWM's acrylic blur-behind (SetAcrylicBlur/DisableBlur, the toggle
    // between them) isn't an instant paint — enabling or disabling it is
    // the compositor's own fade transition, running on DWM's own clock
    // regardless of how fast this process re-issues the API call. Showing
    // the panel right after flipping StyleMode meant the user watched that
    // fade happen live on screen: the old margin/shadow/corners for a
    // moment, then the blur visibly fading in (or out) over roughly this
    // long before settling — which is exactly what read as "it resizes/
    // redraws itself weirdly a second later." See PreviewStyleAsync.
    private static readonly TimeSpan StyleSettleDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// The only entry point Settings' Apariencia live preview should use
    /// for a style/opacity/blur/accent change — NOT the plain
    /// StyleMode-then-Render()-then-Show combination the rest of the app
    /// uses, because a StyleMode change specifically toggles DWM's
    /// undocumented acrylic blur-behind on or off, and that has its own
    /// fade transition (see StyleSettleDelay). Hiding the panel first (if
    /// it was already up), applying the change and repositioning while
    /// still hidden, and only revealing it once the transition has had
    /// time to finish means the user only ever sees the final, correct
    /// frame — never the transition itself. Opacity/blur-percent-only
    /// tweaks (StyleMode unchanged) skip all of that and just update in
    /// place instantly, same as before.
    /// </summary>
    public async Task PreviewStyleAsync(
        PopupWindowStyle style, int opacityPercent, int blurPercent, string? flatBarColorHex,
        IEnumerable<UsageSnapshot> snapshots, bool hasAnyEnabled, DateTime? lastUpdated, int totalEnabled,
        double? besideLeft, double? besideTop, double? besideWidth, double? besideHeight)
    {
        var wasVisible = IsVisible;
        var styleIsChanging = StyleMode != style;
        if (wasVisible && styleIsChanging) Hide();

        StyleMode = style;
        OpacityPercent = opacityPercent;
        BlurPercent = blurPercent;
        FlatBarColorHex = flatBarColorHex;
        Render(snapshots, hasAnyEnabled, lastUpdated, totalEnabled);

        if (wasVisible)
        {
            // Render() already resized/repositioned in place above when the
            // style DIDN'T change (its own ordinary AnimateToNewSize path,
            // since IsVisible was never touched); when it DID change, that
            // branch was skipped because Hide() just ran, so redo it here
            // now that the new margin/content is in place.
            if (styleIsChanging) ResizeInstant();
        }
        else if (besideLeft is { } left && besideTop is { } top && besideWidth is { } width && besideHeight is { } height)
        {
            PositionBesideWindow(left, top, width, height);
        }
        else
        {
            PositionNearCursor();
        }

        if (styleIsChanging) await Task.Delay(StyleSettleDelay);

        if (!IsVisible)
        {
            Show();
            ReapplyDwmStateAfterShow();
        }
        SetPinned(true);
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
        var desired = MeasureRealDesiredSize();
        if (desired is not { } size) return;

        var (_, _, screenTopDip, screenBottomDip) = GetScreenBoundsDip();

        // _maxBottom alone assumes the panel's on-screen position was
        // already valid — true for ordinary content growth (a provider's
        // data landing), but not guaranteed in general, so the target is
        // re-clamped against the real screen bounds too rather than
        // trusting that anchor blindly.
        var newTop = Math.Min(Top, _maxBottom - size.Height);
        newTop = Math.Max(screenTopDip + 8, Math.Min(newTop, screenBottomDip - size.Height - 8));

        var duration = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(WidthProperty, new DoubleAnimation { To = size.Width, Duration = duration, EasingFunction = ease });
        BeginAnimation(HeightProperty, new DoubleAnimation { To = size.Height, Duration = duration, EasingFunction = ease });
        BeginAnimation(TopProperty, new DoubleAnimation { To = newTop, Duration = duration, EasingFunction = ease });
    }

    /// <summary>
    /// Style changes (Standard vs Blur) alter the window's real layout size
    /// by a hard 36px in each dimension the instant ApplyThemeColors runs —
    /// see Render()'s styleChanged branch for why animating that jump (as
    /// AnimateToNewSize does for ordinary content growth) isn't right here:
    /// it left the window mid-resize with the new margin/shadow/DWM-blur
    /// state already applied but the old bounds not yet caught up, which is
    /// what read as a doubled border or a corner getting cut off. Jumping
    /// straight to the correct, screen-clamped bounds in one frame — same
    /// math as AnimateToNewSize, just applied instantly instead of eased —
    /// keeps every visual (margin, shadow, DWM state, window bounds) in
    /// sync on every frame instead of tearing for ~220ms.
    /// </summary>
    private void ResizeInstant()
    {
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        BeginAnimation(TopProperty, null);

        var desired = MeasureRealDesiredSize();
        if (desired is not { } size) return;

        var (screenLeftDip, screenRightDip, screenTopDip, screenBottomDip) = GetScreenBoundsDip();

        var newTop = Math.Min(Top, _maxBottom - size.Height);
        newTop = Math.Max(screenTopDip + 8, Math.Min(newTop, screenBottomDip - size.Height - 8));
        var newLeft = Math.Max(screenLeftDip + 8, Math.Min(Left, screenRightDip - size.Width - 8));

        Width = size.Width;
        Height = size.Height;
        Top = newTop;
        Left = newLeft;
        _maxBottom = Top + Height;
    }

    /// <summary>
    /// Constant chrome reserve (DIPs, one side) added around the card's
    /// natural content size to get the window's real (HWND) size — see
    /// MeasureRealDesiredSize for why this has to be a fixed number rather
    /// than whatever RootGrid's current Margin happens to be.
    /// </summary>
    private const double ChromeReserve = 18;

    /// <summary>
    /// Measuring the WINDOW itself (as this used to do) bakes in whatever
    /// RootGrid.Margin ApplyThemeColors currently has set — 18px in
    /// Standard mode (room for the drop shadow to fade out) but 0 in Blur
    /// mode (so the acrylic tint covers the whole HWND with no untinted
    /// seam — see ApplyThemeColors). That meant the window's real size for
    /// otherwise-identical content differed by a hard 36px between modes,
    /// and every resize/reposition path anchored on the window's raw
    /// Left/Top corner, not the visible card — so switching modes visibly
    /// shifted and resized the card, not just restyled it, which is what
    /// kept reading as a broken resize no matter how the DWM-transition
    /// timing was tuned.
    ///
    /// Measuring RootBorder directly instead — the actual card, before any
    /// outer chrome margin — and always adding the same ChromeReserve on
    /// top regardless of mode makes the window's real size identical for
    /// identical content in both modes. RootGrid's Margin toggle (still 18
    /// vs 0, unchanged) then does the rest for free through ordinary
    /// Stretch-arrange: in Standard mode RootBorder is arranged at exactly
    /// its natural size, inset by the reserve, same as before; in Blur mode
    /// RootBorder stretches to fill the now-uniformly-sized window
    /// edge-to-edge as before, just centered around its natural content
    /// (see the inner Grid's Center alignment in PopupWindow.xaml) instead
    /// of pinned to the top-left, so the extra room reads as an even ring
    /// instead of dead space at the bottom.
    ///
    /// InvalidateMeasure() first forces WPF to actually recompute against
    /// the real current content instead of handing back a stale/degenerate
    /// cached DesiredSize (this produced an ~2x2 result once, which then
    /// made the away-hover hit-test think the whole panel was a single
    /// pixel and closed it on the very next poll). Returns null for an
    /// obviously-wrong tiny result instead of a size neither caller should
    /// ever apply.
    /// </summary>
    private Size? MeasureRealDesiredSize()
    {
        RootGrid.InvalidateMeasure();
        RootBorder.InvalidateMeasure();
        RootBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = RootBorder.DesiredSize;
        return desired.Width < 100 || desired.Height < 50
            ? null
            : new Size(desired.Width + ChromeReserve * 2, desired.Height + ChromeReserve * 2);
    }

    /// <summary>Work area of the monitor the window is currently on, in DIPs — shared by every resize/reposition path.</summary>
    private (double Left, double Right, double Top, double Bottom) GetScreenBoundsDip()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var anchor = new NativeScreenHelper.POINT
        {
            X = (int)((Left + Width / 2) * dpi.DpiScaleX),
            Y = (int)((Top + Height / 2) * dpi.DpiScaleY),
        };
        var screen = NativeScreenHelper.GetWorkAreaForPoint(anchor);
        return (screen.Left / dpi.DpiScaleX, screen.Right / dpi.DpiScaleX, screen.Top / dpi.DpiScaleY, screen.Bottom / dpi.DpiScaleY);
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
