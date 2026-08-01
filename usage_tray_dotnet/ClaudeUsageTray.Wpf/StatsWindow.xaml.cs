using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace ClaudeUsageTray;

public partial class StatsWindow : Window
{
    // Matches PopupWindow's own content width (SingleColumnWidth) exactly,
    // so the two windows read as a matched pair side by side rather than
    // Stats looking like an unrelated dialog.
    private const double ChartWidth = 288;
    private const double ChartHeight = 130;
    private const double AnchorGap = 12;

    private readonly List<string> _serviceNames;
    private readonly UsageHistoryStore _historyStore;
    private readonly Rect _anchorBounds;

    // Same reasoning as SettingsWindow's taskbar icon fields — built once
    // and kept alive for the window's lifetime, sent to Windows via
    // WM_SETICON since WindowStyle="None" windows don't reliably pick up
    // the taskbar icon any other way.
    private readonly System.Drawing.Icon _smallTaskbarIcon = IconFactory.BuildRobotIcon(16);
    private readonly System.Drawing.Icon _bigTaskbarIcon = IconFactory.BuildRobotIcon(32);

    /// <summary>
    /// <paramref name="anchorBounds"/> is the main popup's on-screen bounds
    /// at the moment Stats was opened from it (captured before the popup
    /// hides) — Stats opens just to its left by default.
    /// </summary>
    public StatsWindow(List<string> serviceNames, UsageHistoryStore historyStore, Rect anchorBounds)
    {
        InitializeComponent();
        _serviceNames = serviceNames;
        _historyStore = historyStore;
        _anchorBounds = anchorBounds;
        Title = Strings.T("stats.title");

        // Same lesson as ToastWindow's positioning bug: this window's real
        // size isn't known until after its first layout pass, so it's
        // parked off-screen and only moved into place once Loaded fires
        // with ActualWidth/ActualHeight actually populated.
        Left = -10000;
        Top = -10000;
        Loaded += OnLoadedPosition;

        Render();
    }

    private void OnLoadedPosition(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedPosition;
        Left = _anchorBounds.Left - ActualWidth - AnchorGap;
        Top = _anchorBounds.Top;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // Deliberately NOT calling DwmHelper.EnableRoundedCorners here —
        // this window (like PopupWindow/ToastWindow/AppDialogWindow) draws
        // its own rounded card inside an AllowsTransparency Margin, and
        // asking DWM to ALSO round the actual (invisible, rectangular) HWND
        // is what produced the stray ghost border around the card. Only
        // SettingsWindow uses EnableRoundedCorners, because it's a real
        // opaque window without this transparent-margin trick.
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
