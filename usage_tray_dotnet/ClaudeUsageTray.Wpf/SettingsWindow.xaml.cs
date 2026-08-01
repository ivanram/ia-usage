using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ClaudeUsageTray;

public partial class SettingsWindow : Window
{
    private const string IconGear = "";
    private const string IconRefresh = "";
    private const string IconClock = "";
    private const string IconPalette = "";

    private const double TitleSize = 16;
    private const double BodySize = 14;
    private const double CaptionSize = 12;

    private static readonly int[] HoverDelayValues = { 0, 1, 2, 3 };
    private static string[] HoverDelayLabels => new[] { Strings.T("hoverdelay.instant"), "1s", "2s", "3s" };
    private static string[] ThemeLabels => new[] { Strings.T("theme.system"), Strings.T("theme.light"), Strings.T("theme.dark") };

    private Slider _refreshSlider = null!;
    private TextBox _refreshTextBox = null!;
    private RadioButton _richMode = null!;
    private RadioButton _tooltipMode = null!;
    private readonly Button[] _themeButtons = new Button[3];
    private AppTheme _selectedTheme;
    private readonly Button[] _hoverDelayButtons = new Button[HoverDelayValues.Length];
    private int _selectedHoverDelay;
    private readonly Button[] _accentButtons = new Button[ThemeHelper.AccentSwatches.Length + 1];
    private string _selectedAccent;
    private AppLanguage _selectedLanguage;
    private ToggleButton _autoCheckUpdates = null!;

    // Plain green reads fine on a light card but disappears on a dark one —
    // switch to plain white in dark mode instead (resolved once at
    // construction time, matching how the rest of this window snapshots
    // dark/light rather than live-updating mid-session; see _selectedTheme).
    private bool _isDark;
    private Brush SuccessBrush => _isDark ? Brushes.White : (Brush)new BrushConverter().ConvertFrom("#2E8B57")!;
    private ToggleButton _showClaude = null!;
    private ToggleButton _showChatGpt = null!;
    private ToggleButton _showGrok = null!;
    private ToggleButton _notifyResetClaude = null!;
    private ToggleButton _notifyResetChatGpt = null!;
    private ToggleButton _notifyResetGrok = null!;
    private ToggleButton _notifySound = null!;
    private ToggleButton _telegramEnabled = null!;
    private ToggleButton _telegramNotifyUsage = null!;
    private ToggleButton _telegramNotify80 = null!;
    private TextBox _telegramToken = null!;
    private ToggleButton _autoStart = null!;
    private ToggleButton _animationsEnabled = null!;

    private readonly long? _telegramChatId;
    private readonly Func<string, bool> _isLoggedIn;
    private readonly Action<string> _triggerLogin;
    private readonly Action<AppTheme> _previewTheme;
    private readonly Action<AppLanguage> _previewLanguage;
    // Built in the constructor and sent to Windows later in OnSourceInitialized
    // — building these GDI+ icons right as the native HWND is being created
    // (i.e. doing it inside OnSourceInitialized itself) raced with that setup
    // and left the window's content completely unrendered. Deliberately never
    // disposed: WM_SETICON hands Windows the raw HICON, which it keeps using
    // to paint the taskbar button/Alt-Tab thumbnail for as long as this
    // window lives — a couple KB, cleaned up when the process exits.
    private readonly System.Drawing.Icon _smallTaskbarIcon = IconFactory.BuildRobotIcon(16);
    private readonly System.Drawing.Icon _bigTaskbarIcon = IconFactory.BuildRobotIcon(32);
    public AppSettings Result { get; private set; }
    public bool Saved { get; private set; }

    public SettingsWindow(AppSettings current, Func<string, bool> isLoggedIn, Action<string> triggerLogin, Action<AppTheme> previewTheme, Action<AppLanguage> previewLanguage)
    {
        InitializeComponent();
        Result = current;
        _telegramChatId = current.TelegramChatId;
        _selectedTheme = current.Theme;
        _isDark = ThemeHelper.ResolveIsDark(current.Theme);
        _selectedHoverDelay = current.HoverDelaySeconds;
        _selectedAccent = current.AccentColor;
        _selectedLanguage = current.Language;
        _isLoggedIn = isLoggedIn;
        _triggerLogin = triggerLogin;
        _previewTheme = previewTheme;
        _previewLanguage = previewLanguage;

        // Drawn fresh at 18px (CaptionIcon's actual display size) rather
        // than extracted from the exe and downscaled — scaling a 32px+
        // source down to this size blurred the visor/eyes/antenna into an
        // indistinct dark blob, which read as "the old ugly icon" even
        // though the file itself was correct.
        using var captionIcon = IconFactory.BuildRobotIcon(18);
        CaptionIcon.Source = Imaging.CreateBitmapSourceFromHIcon(captionIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

        ApplyChrome();
        BuildCards();
    }

    /// <summary>Top-level chrome (title, footer buttons, version label) that isn't part of any card — re-applied on language change same as the cards themselves.</summary>
    private void ApplyChrome()
    {
        Title = Strings.T("settings.title");
        TitleTextBlock.Text = Strings.T("settings.title");
        CancelButton.Content = Strings.T("settings.cancel");
        SaveButton.Content = Strings.T("settings.save");
        ToolTipService.SetToolTip(VersionButton, Strings.T("settings.version.tooltip"));

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionButton.Content = version is null ? Strings.T("app.name") : $"{Strings.T("app.name")} {version.Major}.{version.Minor}.{version.Build}";
    }

    private void BuildCards()
    {
        AddCard(GlyphIcon(IconGear), Strings.T("card.general"), BuildGeneralCard);
        AddCard(GlyphIcon(IconPalette), Strings.T("card.appearance"), BuildAppearanceCard);
        AddCard(GlyphIcon(IconClock), Strings.T("card.popup"), BuildPopupModeCard);
        AddCard(GlyphIcon(IconRefresh), Strings.T("card.update"), BuildUpdateCard);
        AddCard(AiBadgeIcon(), Strings.T("card.services"), BuildServicesCard);
        AddCard(new Image { Source = ServiceIcons.TelegramIcon, Width = 16, Height = 16 }, Strings.T("card.telegram"), BuildTelegramCard);
    }

    /// <summary>
    /// Picking a language applies it process-wide immediately (see
    /// _previewLanguage), so this window's own already-built content has to
    /// be thrown away and rebuilt in the new language too — same live-apply
    /// idea as the theme preview, just with no cheaper way to "re-skin" text
    /// that was already baked into TextBlocks at construction time. Any
    /// in-progress edits (slider position, unsaved toggles, a half-typed
    /// Telegram token) are captured into Result first so the rebuild doesn't
    /// silently discard them.
    /// </summary>
    private void RebuildForLanguageChange()
    {
        SnapshotIntoResult();
        CardGrid.Children.Clear();
        CardGrid.RowDefinitions.Clear();
        ApplyChrome();
        BuildCards();
    }

    /// <summary>
    /// Places a card into the next free cell of the 2-column CardGrid. Unlike
    /// a UniformGrid — which forces every single cell across the whole grid
    /// to match the tallest one, leaving huge dead space under short cards —
    /// a plain Grid with Auto RowDefinitions sizes each ROW to only its own
    /// tallest cell, so a short "General" card can sit next to a short
    /// "Actualización" card without both being stretched to match "Servicios"
    /// three rows down. The card itself then stretches to fill that row
    /// height, so both cards in a row always match each other.
    /// </summary>
    private void AddCard(FrameworkElement icon, string title, Action<StackPanel> populate)
    {
        var index = CardGrid.Children.Count;
        var row = index / 2;
        var column = index % 2;
        if (CardGrid.RowDefinitions.Count <= row)
        {
            CardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var card = BuildCard(icon, title, populate);
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
        CardGrid.Children.Add(card);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        DwmHelper.EnableRoundedCorners(hwnd);
        DwmHelper.SetTitleBarDarkMode(hwnd, ThemeHelper.ResolveIsDark(_selectedTheme));
        DwmHelper.SetWindowIcon(hwnd, _smallTaskbarIcon.Handle, _bigTaskbarIcon.Handle);
    }

    private static TextBlock GlyphIcon(string glyph)
    {
        var block = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16 };
        block.SetResourceReference(TextBlock.ForegroundProperty, "MaterialDesignBodyLight");
        return block;
    }

    private FrameworkElement AiBadgeIcon()
    {
        var badge = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10) };
        badge.SetResourceReference(Border.BackgroundProperty, "MaterialDesign.Brush.Primary");
        var text = new TextBlock
        {
            Text = "AI",
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "MaterialDesign.Brush.Primary.Foreground");
        badge.Child = text;
        return badge;
    }

    private static Border BuildCard(FrameworkElement icon, string title, Action<StackPanel> populate)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
        icon.Margin = new Thickness(0, 0, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(icon);
        header.Children.Add(new TextBlock { Text = title, FontSize = TitleSize, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        stack.Children.Add(header);
        populate(stack);

        var card = new Border
        {
            Margin = new Thickness(0, 0, 16, 16),
            Padding = new Thickness(24),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Child = stack,
        };
        card.SetResourceReference(Border.BorderBrushProperty, "MaterialDesignDivider");
        card.SetResourceReference(Border.BackgroundProperty, "MaterialDesignCardBackground");
        return card;
    }

    private static TextBlock Hint(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = CaptionSize,
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "MaterialDesignBodyLight");
        return block;
    }

    private void BuildGeneralCard(StackPanel stack)
    {
        stack.Children.Add(BuildSwitchRow(Strings.T("general.autostart.label"), Strings.T("general.autostart.hint"), AutoStartHelper.IsEnabled(), out _autoStart));
        stack.Children.Add(new Border { Height = 18 });
        stack.Children.Add(BuildSwitchRow(Strings.T("general.animations.label"), Strings.T("general.animations.hint"), Result.AnimationsEnabled, out _animationsEnabled));
        stack.Children.Add(new Border { Height = 18 });
        stack.Children.Add(BuildSwitchRow(Strings.T("general.autoupdate.label"), Strings.T("general.autoupdate.hint"), Result.AutoCheckUpdates, out _autoCheckUpdates));
        stack.Children.Add(new Border { Height = 20 });

        stack.Children.Add(Hint(Strings.T("general.language.label")));
        var (languageRow, _) = BuildSegmented(new[] { "Español", "English" }, SelectLanguage, (int)_selectedLanguage);
        stack.Children.Add(languageRow);
    }

    private void SelectLanguage(int index)
    {
        _selectedLanguage = (AppLanguage)index;
        _previewLanguage(_selectedLanguage);
        RebuildForLanguageChange();
    }

    private void BuildUpdateCard(StackPanel stack)
    {
        var labelRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock { Text = Strings.T("update.frequency.label"), FontSize = BodySize, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(label, 0);

        _refreshTextBox = new TextBox
        {
            Text = Math.Clamp(Result.RefreshMinutes, 1, 60).ToString(),
            Width = 50,
            TextAlignment = TextAlignment.Center,
            Style = (Style)FindResource("FlatTextBox"),
        };
        Grid.SetColumn(_refreshTextBox, 1);

        labelRow.Children.Add(label);
        labelRow.Children.Add(_refreshTextBox);
        stack.Children.Add(labelRow);

        _refreshSlider = new Slider
        {
            Minimum = 1,
            Maximum = 60,
            Value = Math.Clamp(Result.RefreshMinutes, 1, 60),
            Style = (Style)FindResource("FlatSlider"),
            Margin = new Thickness(0, 4, 0, 8),
        };
        _refreshSlider.ValueChanged += (s, e) => _refreshTextBox.Text = ((int)_refreshSlider.Value).ToString();
        _refreshTextBox.LostFocus += (s, e) =>
        {
            if (int.TryParse(_refreshTextBox.Text, out var v))
                _refreshSlider.Value = Math.Clamp(v, 1, 60);
        };
        stack.Children.Add(_refreshSlider);
    }

    private void BuildPopupModeCard(StackPanel stack)
    {
        _tooltipMode = new RadioButton { Content = Strings.T("popup.mode.tooltip"), FontSize = BodySize, GroupName = "PopupMode", IsChecked = Result.PopupMode == PopupMode.Tooltip, Margin = new Thickness(0, 0, 0, 12) };
        _richMode = new RadioButton { Content = Strings.T("popup.mode.rich"), FontSize = BodySize, GroupName = "PopupMode", IsChecked = Result.PopupMode == PopupMode.Rich, Margin = new Thickness(0, 0, 0, 20) };
        stack.Children.Add(_tooltipMode);
        stack.Children.Add(_richMode);

        stack.Children.Add(Hint(Strings.T("popup.mode.hoverhint")));
        var (row, buttons) = BuildSegmented(HoverDelayLabels, i => _selectedHoverDelay = HoverDelayValues[i], Array.IndexOf(HoverDelayValues, _selectedHoverDelay));
        Array.Copy(buttons, _hoverDelayButtons, buttons.Length);
        stack.Children.Add(row);

        // Hover delay only applies to the floating window — greyed out and
        // non-interactive while Tooltip mode is selected, since it does nothing there.
        void UpdateHoverDelayAvailability() => row.IsEnabled = _richMode.IsChecked == true;
        _richMode.Checked += (s, e) => UpdateHoverDelayAvailability();
        _tooltipMode.Checked += (s, e) => UpdateHoverDelayAvailability();
        UpdateHoverDelayAvailability();
    }

    private void BuildAppearanceCard(StackPanel stack)
    {
        var (row, buttons) = BuildSegmented(ThemeLabels, SelectTheme, (int)_selectedTheme);
        Array.Copy(buttons, _themeButtons, buttons.Length);
        stack.Children.Add(row);

        stack.Children.Add(new Border { Height = 20 });
        stack.Children.Add(Hint(Strings.T("appearance.accent.hint")));

        var swatches = new UniformGrid { Columns = 6, Margin = new Thickness(0, 4, 0, 0) };

        var originalSwatch = BuildAccentSwatch(OriginalGradientBrush(), AppSettings.OriginalAccentSentinel);
        _accentButtons[0] = originalSwatch;
        swatches.Children.Add(originalSwatch);

        for (var i = 0; i < ThemeHelper.AccentSwatches.Length; i++)
        {
            var hex = ThemeHelper.AccentSwatches[i];
            var swatch = BuildAccentSwatch((Brush)new BrushConverter().ConvertFrom(hex)!, hex);
            _accentButtons[i + 1] = swatch;
            swatches.Children.Add(swatch);
        }
        stack.Children.Add(swatches);
    }

    private Button BuildAccentSwatch(Brush background, string value)
    {
        var swatch = new Button
        {
            Style = (Style)FindResource("ColorSwatchButton"),
            Background = background,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10),
            Tag = value == _selectedAccent ? "Selected" : null,
        };
        swatch.Click += (s, e) => SelectAccent(value);
        return swatch;
    }

    private static Brush OriginalGradientBrush() => new LinearGradientBrush
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(1, 1),
        GradientStops = new GradientStopCollection
        {
            new GradientStop((Color)ColorConverter.ConvertFromString("#2E8B57"), 0),
            new GradientStop((Color)ColorConverter.ConvertFromString("#D4A017"), 0.55),
            new GradientStop((Color)ColorConverter.ConvertFromString("#D64545"), 1),
        },
    };

    private void SelectTheme(int index)
    {
        _selectedTheme = (AppTheme)index;
        StyleSegmented(_themeButtons, index);
        _previewTheme(_selectedTheme);
        // The "Original" accent has separate light/dark variants (see
        // ThemeHelper.ApplyAccent) — re-applying here picks up the right
        // one now that the base theme just changed, instead of leaving
        // whichever variant was set for the previous base theme.
        ThemeHelper.ApplyAccent(_selectedAccent);
        DwmHelper.SetTitleBarDarkMode(new WindowInteropHelper(this).Handle, ThemeHelper.ResolveIsDark(_selectedTheme));
    }

    private void SelectAccent(string value)
    {
        _selectedAccent = value;
        for (var i = 0; i < _accentButtons.Length; i++)
        {
            var swatchValue = i == 0 ? AppSettings.OriginalAccentSentinel : ThemeHelper.AccentSwatches[i - 1];
            _accentButtons[i].Tag = swatchValue == value ? "Selected" : null;
        }
        ThemeHelper.ApplyAccent(value);
    }

    private (UniformGrid Row, Button[] Buttons) BuildSegmented(string[] labels, Action<int> onSelect, int selectedIndex)
    {
        var row = new UniformGrid { Rows = 1, Columns = labels.Length };
        var buttons = new Button[labels.Length];
        for (var i = 0; i < labels.Length; i++)
        {
            var idx = i;
            var btn = new Button
            {
                Content = labels[i],
                Margin = new Thickness(i == 0 ? 0 : 4, 0, i == labels.Length - 1 ? 0 : 4, 0),
            };
            btn.Click += (s, e) =>
            {
                onSelect(idx);
                StyleSegmented(buttons, idx);
            };
            row.Children.Add(btn);
            buttons[i] = btn;
        }
        StyleSegmented(buttons, selectedIndex);
        return (row, buttons);
    }

    private void StyleSegmented(Button[] buttons, int selectedIndex)
    {
        for (var i = 0; i < buttons.Length; i++)
        {
            buttons[i].Style = (Style)FindResource(i == selectedIndex ? "SegmentedButtonSelected" : "SegmentedButton");
        }
    }

    private void BuildServicesCard(StackPanel stack)
    {
        stack.Children.Add(BuildServiceRow("Claude", Result.ShowClaude, Result.NotifyResetClaude, out _showClaude, out _notifyResetClaude));
        stack.Children.Add(new Border { Height = 18 });
        stack.Children.Add(BuildServiceRow("ChatGPT", Result.ShowChatGpt, Result.NotifyResetChatGpt, out _showChatGpt, out _notifyResetChatGpt));
        stack.Children.Add(new Border { Height = 18 });
        stack.Children.Add(BuildServiceRow("Grok", Result.ShowGrok, Result.NotifyResetGrok, out _showGrok, out _notifyResetGrok));
        stack.Children.Add(new Border { Height = 22 });

        stack.Children.Add(BuildSwitchRow(Strings.T("service.sound.label"), Strings.T("service.sound.hint"), Result.NotifySoundEnabled, out _notifySound));
        UpdateSoundToggleAvailability();
        foreach (var t in new[] { _notifyResetClaude, _notifyResetChatGpt, _notifyResetGrok })
        {
            t.Checked += (s, e) => UpdateSoundToggleAvailability();
            t.Unchecked += (s, e) => UpdateSoundToggleAvailability();
        }
    }

    private void UpdateSoundToggleAvailability()
    {
        var anyNotifyEnabled = _notifyResetClaude.IsChecked == true || _notifyResetChatGpt.IsChecked == true
            || _notifyResetGrok.IsChecked == true;
        _notifySound.IsEnabled = anyNotifyEnabled;
        if (!anyNotifyEnabled) _notifySound.IsChecked = false;
    }

    /// <summary>
    /// Two stacked rows: [icon+name] and [status/link + bell], with the
    /// bell sitting immediately to the right of the status text (not out
    /// by the switch) so the two read as one unit. The main show/hide
    /// switch stays alone on the right, spanning both rows.
    /// </summary>
    private FrameworkElement BuildServiceRow(string providerName, bool shown, bool notifyReset, out ToggleButton showToggle, out ToggleButton notifyToggle)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        var serviceIcon = ServiceIcons.Build(providerName, 16, (Brush)FindResource("MaterialDesignBody"));
        serviceIcon.Margin = new Thickness(0, 0, 8, 0);
        serviceIcon.VerticalAlignment = VerticalAlignment.Center;
        nameRow.Children.Add(serviceIcon);
        nameRow.Children.Add(new TextBlock { Text = providerName, FontSize = BodySize, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetRow(nameRow, 0);
        Grid.SetColumn(nameRow, 0);

        // A fixed-width status column (rather than letting the bell just
        // trail the text in a StackPanel) keeps the bell's left edge at the
        // same X in every row — "Sesión iniciada" and "Vincular cuenta"
        // render at different widths (and the link button carries its own
        // chrome/padding on top), so without this the bells drifted left
        // or right row to row instead of stacking in a clean column.
        var statusRow = new Grid { Margin = new Thickness(24, 6, 0, 0) };
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement status;
        if (_isLoggedIn(providerName))
        {
            status = new TextBlock { Text = Strings.T("service.loggedin"), FontSize = CaptionSize, VerticalAlignment = VerticalAlignment.Center, Foreground = SuccessBrush };
        }
        else
        {
            var link = new Button { Content = Strings.T("service.link"), Style = (Style)FindResource("LinkButton"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0) };
            link.Click += (s, e) => _triggerLogin(providerName);
            status = link;
        }
        Grid.SetColumn(status, 0);
        statusRow.Children.Add(status);

        var notifyToggleLocal = new ToggleButton { IsChecked = notifyReset, Style = (Style)FindResource("BellToggle"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0) };
        ToolTipService.SetToolTip(notifyToggleLocal, Strings.T("service.notify.tooltip"));
        Grid.SetColumn(notifyToggleLocal, 1);
        statusRow.Children.Add(notifyToggleLocal);
        Grid.SetRow(statusRow, 1);
        Grid.SetColumn(statusRow, 0);

        var showToggleLocal = new ToggleButton { IsChecked = shown, Style = (Style)FindResource("FlatSwitch"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(showToggleLocal, 1);
        Grid.SetRowSpan(showToggleLocal, 2);

        row.Children.Add(nameRow);
        row.Children.Add(statusRow);
        row.Children.Add(showToggleLocal);

        showToggle = showToggleLocal;
        notifyToggle = notifyToggleLocal;
        return row;
    }

    private void BuildTelegramCard(StackPanel stack)
    {
        stack.Children.Add(BuildSwitchRow(Strings.T("telegram.enable.label"), null, Result.TelegramEnabled, out _telegramEnabled));

        _telegramToken = new TextBox { Text = Result.TelegramBotToken ?? "", Style = (Style)FindResource("FlatTextBox"), Margin = new Thickness(0, 14, 0, 0) };
        stack.Children.Add(_telegramToken);

        _telegramEnabled.Checked += (s, e) => _telegramToken.IsEnabled = true;
        _telegramEnabled.Unchecked += (s, e) => _telegramToken.IsEnabled = false;
        _telegramToken.IsEnabled = _telegramEnabled.IsChecked == true;

        stack.Children.Add(new Border { Height = 16 });

        if (Result.TelegramChatId is not null)
        {
            var linked = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            linked.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = SuccessBrush, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            linked.Children.Add(new TextBlock { Text = Strings.T("telegram.linked"), FontSize = CaptionSize, VerticalAlignment = VerticalAlignment.Center, Foreground = SuccessBrush });
            stack.Children.Add(linked);
            stack.Children.Add(BulletPoint(Strings.T("telegram.linked.hint")));
        }
        else
        {
            stack.Children.Add(Hint(Strings.T("telegram.setup.title")));
            stack.Children.Add(BulletPoint(Strings.T("telegram.setup.1")));
            stack.Children.Add(BulletPoint(Strings.T("telegram.setup.2")));
            stack.Children.Add(BulletPoint(Strings.T("telegram.setup.3")));
            stack.Children.Add(BulletPoint(Strings.T("telegram.setup.4")));
        }

        stack.Children.Add(new Border { Height = 18 });
        stack.Children.Add(BuildSwitchRow(Strings.T("telegram.notifyusage.label"), Strings.T("telegram.notifyusage.hint"), Result.TelegramNotifyUsage, out _telegramNotifyUsage));
        stack.Children.Add(new Border { Height = 18 });
        stack.Children.Add(BuildSwitchRow(Strings.T("telegram.notify80.label"), Strings.T("telegram.notify80.hint"), Result.TelegramNotify80Percent, out _telegramNotify80));

        void UpdateNotify80Availability() => _telegramNotify80.IsEnabled = _telegramNotifyUsage.IsChecked == true;
        _telegramNotifyUsage.Checked += (s, e) => UpdateNotify80Availability();
        _telegramNotifyUsage.Unchecked += (s, e) =>
        {
            _telegramNotify80.IsChecked = false;
            UpdateNotify80Availability();
        };
        UpdateNotify80Availability();
    }

    private static TextBlock BulletPoint(string text) => new()
    {
        Text = text,
        FontSize = BodySize,
        Margin = new Thickness(0, 6, 0, 0),
        TextWrapping = TextWrapping.Wrap,
    };

    private FrameworkElement BuildSwitchRow(string label, string? hint, bool initial, out ToggleButton toggle)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        textStack.Children.Add(new TextBlock { Text = label, FontSize = BodySize, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5) });
        if (hint is not null) textStack.Children.Add(Hint(hint));
        Grid.SetColumn(textStack, 0);

        var toggleLocal = new ToggleButton { IsChecked = initial, VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("FlatSwitch") };
        Grid.SetColumn(toggleLocal, 1);

        row.Children.Add(textStack);
        row.Children.Add(toggleLocal);
        toggle = toggleLocal;
        return row;
    }

    /// <summary>
    /// Reads every control's current value into Result — the single source
    /// of truth both OnSaveClick and a language-change rebuild use, so an
    /// in-progress language switch never loses whatever else the user was
    /// mid-editing.
    /// </summary>
    private void SnapshotIntoResult()
    {
        var newToken = string.IsNullOrWhiteSpace(_telegramToken.Text) ? null : _telegramToken.Text.Trim();
        var tokenChanged = newToken != Result.TelegramBotToken;

        Result = new AppSettings
        {
            RefreshMinutes = (int)_refreshSlider.Value,
            PopupMode = _richMode.IsChecked == true ? PopupMode.Rich : PopupMode.Tooltip,
            HoverDelaySeconds = _selectedHoverDelay,
            Theme = _selectedTheme,
            AccentColor = _selectedAccent,
            AnimationsEnabled = _animationsEnabled.IsChecked == true,
            ShowClaude = _showClaude.IsChecked == true,
            ShowChatGpt = _showChatGpt.IsChecked == true,
            ShowGrok = _showGrok.IsChecked == true,
            NotifyResetClaude = _notifyResetClaude.IsChecked == true,
            NotifyResetChatGpt = _notifyResetChatGpt.IsChecked == true,
            NotifyResetGrok = _notifyResetGrok.IsChecked == true,
            NotifySoundEnabled = _notifySound.IsChecked == true,
            TelegramEnabled = _telegramEnabled.IsChecked == true,
            TelegramBotToken = newToken,
            TelegramChatId = tokenChanged ? null : _telegramChatId,
            TelegramNotifyUsage = _telegramNotifyUsage.IsChecked == true,
            TelegramNotify80Percent = _telegramNotify80.IsChecked == true,
            Language = _selectedLanguage,
            AutoCheckUpdates = _autoCheckUpdates.IsChecked == true,
            // Not surfaced in the UI — carried forward so a save right
            // after dismissing the update dialog with "Hoy no, mañana"
            // doesn't wipe out that snooze.
            UpdateSnoozeUntil = Result.UpdateSnoozeUntil,
            LastUpdateCheckAt = Result.LastUpdateCheckAt,
        };
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        AutoStartHelper.SetEnabled(_autoStart.IsChecked == true);
        SnapshotIntoResult();
        Saved = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Saved = false;
        Close();
    }

    private async void OnVersionClick(object sender, RoutedEventArgs e)
    {
        VersionButton.IsEnabled = false;
        var original = VersionButton.Content;
        VersionButton.Content = Strings.T("settings.version.checking");
        try
        {
            await UpdateService.CheckAndPromptAsync(manualCheck: true);
        }
        finally
        {
            VersionButton.Content = original;
            VersionButton.IsEnabled = true;
        }
    }
}
