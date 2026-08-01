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
    private static readonly string[] HoverDelayLabels = { "Instantáneo", "1s", "2s", "3s" };
    private static readonly string[] ThemeLabels = { "Sistema", "Claro", "Oscuro" };

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

    private ToggleButton _showClaude = null!;
    private ToggleButton _showChatGpt = null!;
    private ToggleButton _showGrok = null!;
    private ToggleButton _notifyResetClaude = null!;
    private ToggleButton _notifyResetChatGpt = null!;
    private ToggleButton _notifyResetGrok = null!;
    private ToggleButton _notifySound = null!;
    private ToggleButton _telegramEnabled = null!;
    private TextBox _telegramToken = null!;
    private ToggleButton _autoStart = null!;
    private ToggleButton _animationsEnabled = null!;

    private readonly long? _telegramChatId;
    private readonly Func<string, bool> _isLoggedIn;
    private readonly Action<string> _triggerLogin;
    private readonly Action<AppTheme> _previewTheme;
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

    public SettingsWindow(AppSettings current, Func<string, bool> isLoggedIn, Action<string> triggerLogin, Action<AppTheme> previewTheme)
    {
        InitializeComponent();
        Result = current;
        _telegramChatId = current.TelegramChatId;
        _selectedTheme = current.Theme;
        _selectedHoverDelay = current.HoverDelaySeconds;
        _selectedAccent = current.AccentColor;
        _isLoggedIn = isLoggedIn;
        _triggerLogin = triggerLogin;
        _previewTheme = previewTheme;

        // Drawn fresh at 18px (CaptionIcon's actual display size) rather
        // than extracted from the exe and downscaled — scaling a 32px+
        // source down to this size blurred the visor/eyes/antenna into an
        // indistinct dark blob, which read as "the old ugly icon" even
        // though the file itself was correct.
        using var captionIcon = IconFactory.BuildRobotIcon(18);
        CaptionIcon.Source = Imaging.CreateBitmapSourceFromHIcon(captionIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionButton.Content = version is null ? "Uso de IA" : $"Uso de IA {version.Major}.{version.Minor}.{version.Build}";

        AddCard(GlyphIcon(IconGear), "General", BuildGeneralCard);
        AddCard(GlyphIcon(IconRefresh), "Actualización", BuildUpdateCard);
        AddCard(GlyphIcon(IconClock), "Panel emergente", BuildPopupModeCard);
        AddCard(GlyphIcon(IconPalette), "Apariencia", BuildAppearanceCard);
        AddCard(AiBadgeIcon(), "Servicios", BuildServicesCard);
        AddCard(new Image { Source = ServiceIcons.TelegramIcon, Width = 16, Height = 16 }, "Bot de Telegram", BuildTelegramCard);
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
        stack.Children.Add(BuildSwitchRow("Iniciar con Windows", "Abre la app automáticamente al encender el equipo", AutoStartHelper.IsEnabled(), out _autoStart));
        stack.Children.Add(new Border { Height = 18 });
        stack.Children.Add(BuildSwitchRow("Animaciones", "Anima las barras de progreso del panel emergente", Result.AnimationsEnabled, out _animationsEnabled));
    }

    private void BuildUpdateCard(StackPanel stack)
    {
        var labelRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock { Text = "Frecuencia de actualización (en minutos)", FontSize = BodySize, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
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
        _tooltipMode = new RadioButton { Content = "Tooltip sencillo", FontSize = BodySize, GroupName = "PopupMode", IsChecked = Result.PopupMode == PopupMode.Tooltip, Margin = new Thickness(0, 0, 0, 12) };
        _richMode = new RadioButton { Content = "Ventana flotante (recomendado)", FontSize = BodySize, GroupName = "PopupMode", IsChecked = Result.PopupMode == PopupMode.Rich, Margin = new Thickness(0, 0, 0, 20) };
        stack.Children.Add(_tooltipMode);
        stack.Children.Add(_richMode);

        stack.Children.Add(Hint("Mostrar panel al pasar el ratón por encima"));
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
        stack.Children.Add(Hint("Color de acento — \"Original\" colorea las barras del panel según el % de uso"));

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

        stack.Children.Add(BuildSwitchRow("Recibir notificación con sonido", "Se te notificará con sonido cuando se reinicie el uso", Result.NotifySoundEnabled, out _notifySound));
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
            status = SuccessBadge("Sesión iniciada");
        }
        else
        {
            var link = new Button { Content = "Vincular cuenta", Style = (Style)FindResource("LinkButton"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0) };
            link.Click += (s, e) => _triggerLogin(providerName);
            status = link;
        }
        Grid.SetColumn(status, 0);
        statusRow.Children.Add(status);

        var notifyToggleLocal = new ToggleButton { IsChecked = notifyReset, Style = (Style)FindResource("BellToggle"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0) };
        ToolTipService.SetToolTip(notifyToggleLocal, "Avisar cuando se reinicie el límite de uso");
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

    // A fixed green fill + white text (rather than green text on the
    // card's own background) reads fine in both light and dark mode —
    // plain green text was too low-contrast against a light card no
    // matter which shade of green got picked.
    private static Border SuccessBadge(string text) => new()
    {
        Background = (Brush)new BrushConverter().ConvertFrom("#2E8B57")!,
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(9, 3, 9, 3),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = CaptionSize, Foreground = Brushes.White },
    };

    private void BuildTelegramCard(StackPanel stack)
    {
        stack.Children.Add(BuildSwitchRow("Activar bot de Telegram", null, Result.TelegramEnabled, out _telegramEnabled));

        _telegramToken = new TextBox { Text = Result.TelegramBotToken ?? "", Style = (Style)FindResource("FlatTextBox"), Margin = new Thickness(0, 14, 0, 0) };
        stack.Children.Add(_telegramToken);

        _telegramEnabled.Checked += (s, e) => _telegramToken.IsEnabled = true;
        _telegramEnabled.Unchecked += (s, e) => _telegramToken.IsEnabled = false;
        _telegramToken.IsEnabled = _telegramEnabled.IsChecked == true;

        stack.Children.Add(new Border { Height = 16 });

        if (Result.TelegramChatId is not null)
        {
            var linked = SuccessBadge("Chat vinculado");
            linked.Margin = new Thickness(0, 0, 0, 10);
            stack.Children.Add(linked);
            stack.Children.Add(BulletPoint("Escríbele /uso al bot en cualquier momento para consultar tu consumo actual de todos los servicios activos, directamente desde Telegram."));
        }
        else
        {
            stack.Children.Add(Hint("Para vincular el bot:"));
            stack.Children.Add(BulletPoint("1. Abre Telegram y busca a @BotFather."));
            stack.Children.Add(BulletPoint("2. Envíale /newbot y sigue los pasos para crear tu bot."));
            stack.Children.Add(BulletPoint("3. Pega aquí el token que te entregue y guarda los ajustes."));
            stack.Children.Add(BulletPoint("4. Escríbele /uso a tu nuevo bot para vincular el chat."));
        }
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

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        AutoStartHelper.SetEnabled(_autoStart.IsChecked == true);

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
        };
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
        VersionButton.Content = "Buscando actualizaciones...";
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
