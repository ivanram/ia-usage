using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ClaudeUsageTray;

public partial class SettingsWindow : Window
{
    private const double CardWidth = 330;

    private Slider _refreshSlider = null!;
    private TextBox _refreshTextBox = null!;
    private RadioButton _richMode = null!;
    private RadioButton _tooltipMode = null!;
    private readonly Button[] _themeButtons = new Button[3];
    private AppTheme _selectedTheme;
    private ToggleButton _showClaude = null!;
    private ToggleButton _showChatGpt = null!;
    private ToggleButton _telegramEnabled = null!;
    private TextBox _telegramToken = null!;
    private ToggleButton _autoStart = null!;

    private readonly long? _telegramChatId;
    private readonly Func<string, bool> _isLoggedIn;
    private readonly Action<string> _triggerLogin;
    public AppSettings Result { get; private set; }
    public bool Saved { get; private set; }

    public SettingsWindow(AppSettings current, Func<string, bool> isLoggedIn, Action<string> triggerLogin)
    {
        InitializeComponent();
        Result = current;
        _telegramChatId = current.TelegramChatId;
        _selectedTheme = current.Theme;
        _isLoggedIn = isLoggedIn;
        _triggerLogin = triggerLogin;

        CardHost.Children.Add(BuildCard("General", BuildGeneralCard));
        CardHost.Children.Add(BuildCard("Actualización", BuildUpdateCard));
        CardHost.Children.Add(BuildCard("Modo de activación", BuildPopupModeCard));
        CardHost.Children.Add(BuildCard("Apariencia", BuildAppearanceCard));
        CardHost.Children.Add(BuildCard("Servicios a mostrar", BuildServicesCard));
        CardHost.Children.Add(BuildCard("Bot de Telegram", BuildTelegramCard));
    }

    private static Border BuildCard(string title, Action<StackPanel> populate)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 0, 0, 14),
        });
        populate(stack);

        var card = new Border
        {
            Width = CardWidth,
            Margin = new Thickness(0, 0, 14, 14),
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(10),
            Background = (Brush)Application.Current.FindResource("MaterialDesignCardBackground"),
            Child = stack,
        };
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 10,
            ShadowDepth = 1,
            Opacity = 0.15,
            Color = Colors.Black,
        };
        return card;
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = (Brush)Application.Current.FindResource("MaterialDesignBodyLight"),
        Margin = new Thickness(0, 0, 0, 6),
        TextWrapping = TextWrapping.Wrap,
    };

    private void BuildGeneralCard(StackPanel stack)
    {
        _autoStart = BuildSwitch("Iniciar automáticamente con Windows", AutoStartHelper.IsEnabled());
        stack.Children.Add(_autoStart);
    }

    private void BuildUpdateCard(StackPanel stack)
    {
        stack.Children.Add(Hint("Frecuencia de actualización"));

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _refreshSlider = new Slider
        {
            Minimum = 1,
            Maximum = 60,
            Value = Math.Clamp(Result.RefreshMinutes, 1, 60),
            TickFrequency = 5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        Grid.SetColumn(_refreshSlider, 0);

        _refreshTextBox = new TextBox
        {
            Text = Math.Clamp(Result.RefreshMinutes, 1, 60).ToString(),
            Width = 46,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_refreshTextBox, 1);

        _refreshSlider.ValueChanged += (s, e) => _refreshTextBox.Text = ((int)_refreshSlider.Value).ToString();
        _refreshTextBox.LostFocus += (s, e) =>
        {
            if (int.TryParse(_refreshTextBox.Text, out var v))
                _refreshSlider.Value = Math.Clamp(v, 1, 60);
        };

        row.Children.Add(_refreshSlider);
        row.Children.Add(_refreshTextBox);
        stack.Children.Add(row);
        stack.Children.Add(new TextBlock { Text = "minutos", FontSize = 11, Foreground = (Brush)Application.Current.FindResource("MaterialDesignBodyLight"), Margin = new Thickness(0, 4, 0, 0) });
    }

    private void BuildPopupModeCard(StackPanel stack)
    {
        _richMode = new RadioButton { Content = "Ventana flotante (recomendado)", GroupName = "PopupMode", IsChecked = Result.PopupMode == PopupMode.Rich, Margin = new Thickness(0, 0, 0, 10) };
        _tooltipMode = new RadioButton { Content = "Tooltip sencillo", GroupName = "PopupMode", IsChecked = Result.PopupMode == PopupMode.Tooltip };
        stack.Children.Add(_richMode);
        stack.Children.Add(_tooltipMode);
    }

    private void BuildAppearanceCard(StackPanel stack)
    {
        var row = new UniformGrid { Rows = 1, Columns = 3 };
        string[] labels = { "Sistema", "Claro", "Oscuro" };
        for (var i = 0; i < 3; i++)
        {
            var theme = (AppTheme)i;
            var btn = new Button
            {
                Content = labels[i],
                Margin = new Thickness(i == 0 ? 0 : 3, 0, i == 2 ? 0 : 3, 0),
                Padding = new Thickness(0, 8, 0, 8),
            };
            btn.Click += (s, e) => SelectTheme(theme);
            row.Children.Add(btn);
            _themeButtons[i] = btn;
        }
        StyleThemeButtons();
        stack.Children.Add(row);
    }

    private void SelectTheme(AppTheme theme)
    {
        _selectedTheme = theme;
        StyleThemeButtons();
    }

    private void StyleThemeButtons()
    {
        for (var i = 0; i < _themeButtons.Length; i++)
        {
            var btn = _themeButtons[i];
            if (btn is null) continue;
            var selected = (int)_selectedTheme == i;
            btn.SetResourceReference(StyleProperty, selected ? "MaterialDesignRaisedButton" : "MaterialDesignOutlinedButton");
        }
    }

    private void BuildServicesCard(StackPanel stack)
    {
        _showClaude = BuildSwitch("Claude", Result.ShowClaude);
        _showChatGpt = BuildSwitch("ChatGPT", Result.ShowChatGpt);
        stack.Children.Add(BuildServiceRow(_showClaude, "Claude"));
        stack.Children.Add(BuildServiceRow(_showChatGpt, "ChatGPT"));
    }

    private FrameworkElement BuildServiceRow(ToggleButton toggle, string providerName)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(toggle, 0);
        row.Children.Add(toggle);

        FrameworkElement status;
        if (_isLoggedIn(providerName))
        {
            status = new TextBlock { Text = "✓ Sesión iniciada", FontSize = 11, Foreground = (Brush)new BrushConverter().ConvertFrom("#2E8B57")!, VerticalAlignment = VerticalAlignment.Center };
        }
        else
        {
            var link = new Button { Content = "Vincular", FontSize = 11, Padding = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };
            link.SetResourceReference(StyleProperty, "MaterialDesignFlatButton");
            link.Click += (s, e) => _triggerLogin(providerName);
            status = link;
        }
        Grid.SetColumn(status, 1);
        row.Children.Add(status);
        return row;
    }

    private void BuildTelegramCard(StackPanel stack)
    {
        _telegramEnabled = BuildSwitch("Activar bot de Telegram", Result.TelegramEnabled);
        stack.Children.Add(_telegramEnabled);
        stack.Children.Add(Hint("Escríbele /uso al bot después de guardar para vincular tu chat."));

        _telegramToken = new TextBox { Text = Result.TelegramBotToken ?? "", Margin = new Thickness(0, 4, 0, 0) };
        MaterialDesignThemes.Wpf.HintAssist.SetHint(_telegramToken, "Token de @BotFather");
        stack.Children.Add(_telegramToken);

        _telegramEnabled.Checked += (s, e) => _telegramToken.IsEnabled = true;
        _telegramEnabled.Unchecked += (s, e) => _telegramToken.IsEnabled = false;
        _telegramToken.IsEnabled = _telegramEnabled.IsChecked == true;

        if (Result.TelegramChatId is not null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Chat vinculado ✓",
                FontSize = 11,
                Foreground = (Brush)new BrushConverter().ConvertFrom("#2E8B57")!,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }
    }

    private static ToggleButton BuildSwitch(string label, bool isChecked)
    {
        var toggle = new ToggleButton { Content = label, IsChecked = isChecked, Margin = new Thickness(0, 0, 0, 4) };
        toggle.SetResourceReference(StyleProperty, "MaterialDesignSwitchToggleButton");
        return toggle;
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
            Theme = _selectedTheme,
            ShowClaude = _showClaude.IsChecked == true,
            ShowChatGpt = _showChatGpt.IsChecked == true,
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
}
