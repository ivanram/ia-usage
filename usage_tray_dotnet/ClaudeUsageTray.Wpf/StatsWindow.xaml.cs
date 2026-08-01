using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace ClaudeUsageTray;

public partial class StatsWindow : Window
{
    private const double ChartWidth = 380;
    private const double ChartHeight = 130;

    private readonly List<string> _serviceNames;
    private readonly UsageHistoryStore _historyStore;

    // Same reasoning as SettingsWindow's taskbar icon fields — built once
    // and kept alive for the window's lifetime, sent to Windows via
    // WM_SETICON since WindowStyle="None" windows don't reliably pick up
    // the taskbar icon any other way.
    private readonly System.Drawing.Icon _smallTaskbarIcon = IconFactory.BuildRobotIcon(16);
    private readonly System.Drawing.Icon _bigTaskbarIcon = IconFactory.BuildRobotIcon(32);

    public StatsWindow(List<string> serviceNames, UsageHistoryStore historyStore)
    {
        InitializeComponent();
        _serviceNames = serviceNames;
        _historyStore = historyStore;
        Title = Strings.T("stats.title");
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

    private void OnRootMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { /* mouse released mid-drag */ }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void Render()
    {
        ContentHost.Children.Clear();
        RootBorder.SetResourceReference(Border.BackgroundProperty, "MaterialDesignPaper");

        var textPrimary = (Brush)FindResource("MaterialDesignBody");
        var textSecondary = (Brush)FindResource("MaterialDesignBodyLight");
        var accent = (Brush)FindResource("MaterialDesign.Brush.Primary");
        var gridBrush = (Brush)FindResource("MaterialDesignDivider");
        var fillBrush = accent.Clone();
        fillBrush.Opacity = 0.16;

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        titleRow.Children.Add(new TextBlock
        {
            Text = Strings.T("stats.title"),
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = textPrimary,
        });
        ContentHost.Children.Add(titleRow);

        ContentHost.Children.Add(new TextBlock
        {
            Text = Strings.T("stats.subtitle"),
            FontSize = 12,
            Foreground = textSecondary,
            Margin = new Thickness(0, 0, 0, 18),
        });

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
        var blocks = ChartBuilder.BuildServiceBlocks(_serviceNames, _historyStore, since, ChartWidth, ChartHeight, textPrimary, textSecondary, accent, fillBrush, gridBrush);
        ContentHost.Children.Add(blocks);
    }
}
