using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MaterialDesignThemes.Wpf;

namespace ClaudeUsageTray;

public partial class ToastWindow : Window
{
    private static int _openCount;

    // If the user has been away from the PC for at least this long, the
    // toast waits for them to come back (mouse/keyboard input) instead of
    // auto-closing on a timer nobody was there to see.
    private const double IdleThresholdSeconds = 30;
    private const double VisibleSeconds = 6;

    public ToastWindow()
    {
        InitializeComponent();
    }

    private void OnClick(object sender, RoutedEventArgs e) => CloseForReal();

    public void ShowNear(string serviceName, string message)
    {
        var isDark = ThemeHelper.IsCurrentThemeDark();
        RootBorder.Background = isDark
            ? new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2E))
            : new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
        var textBrush = isDark
            ? new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2))
            : new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));

        ContentHost.Children.Clear();
        var icon = ServiceIcons.Build(serviceName, 20, textBrush);
        icon.Margin = new Thickness(0, 0, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        ContentHost.Children.Add(icon);
        ContentHost.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 260,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = textBrush,
        });

        // Measure()'d DesiredSize before the window has ever been shown was
        // unreliable specifically for a Window (not a plain UIElement) very
        // early in the process's life — the very first toast a fresh launch
        // shows (the "already at 100%" case) consistently measured as 0x0,
        // so it opened, fully "visible", at a computed position entirely
        // off the corner of the screen. SizeToContent already sizes the
        // window correctly through WPF's own layout pass — Loaded is where
        // ActualWidth/ActualHeight are guaranteed to reflect that. Parked
        // off-screen until then so there's no visible jump once positioned.
        Left = -10000;
        Top = -10000;
        Loaded += OnLoadedPositionAndArm;
        Show();
    }

    private void OnLoadedPositionAndArm(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedPositionAndArm;

        var cursor = NativeScreenHelper.GetCursorPosition();
        var screen = NativeScreenHelper.GetWorkAreaForPoint(cursor);
        var dpi = VisualTreeHelper.GetDpi(this);
        var screenRightDip = screen.Right / dpi.DpiScaleX;
        var screenBottomDip = screen.Bottom / dpi.DpiScaleY;

        var stackOffset = _openCount * (ActualHeight + 10);
        Left = screenRightDip - ActualWidth - 12;
        Top = screenBottomDip - ActualHeight - 12 - stackOffset;

        try
        {
            File.AppendAllText(Path.Combine(Paths.LogsDir, "notify_debug.txt"),
                $"{DateTime.Now:O} Toast positioned: ActualWidth={ActualWidth} ActualHeight={ActualHeight} Left={Left} Top={Top} screenRightDip={screenRightDip} screenBottomDip={screenBottomDip}\n");
        }
        catch { /* best effort */ }

        _openCount++;

        if (NativeScreenHelper.GetIdleSeconds() >= IdleThresholdSeconds)
        {
            WaitForActivityThenAutoClose();
        }
        else
        {
            StartAutoCloseTimer();
        }
    }

    private void StartAutoCloseTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(VisibleSeconds) };
        timer.Tick += (s, e) => { timer.Stop(); CloseForReal(); };
        timer.Start();
    }

    /// <summary>
    /// Polls for the user coming back to the PC (idle time dropping to
    /// near zero) before starting the normal auto-close countdown, so a
    /// reset toast shown while nobody's at the keyboard stays up until
    /// someone is actually there to see it.
    /// </summary>
    private void WaitForActivityThenAutoClose()
    {
        var poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        poll.Tick += (s, e) =>
        {
            if (!IsVisible) { poll.Stop(); return; }
            if (NativeScreenHelper.GetIdleSeconds() < 2)
            {
                poll.Stop();
                StartAutoCloseTimer();
            }
        };
        poll.Start();
    }

    private void CloseForReal()
    {
        if (!IsVisible) return;
        _openCount = Math.Max(0, _openCount - 1);
        Close();
    }
}
