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
        var isDark = new PaletteHelper().GetTheme().GetBaseTheme() == BaseTheme.Dark;
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

        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var cursor = NativeScreenHelper.GetCursorPosition();
        var screen = NativeScreenHelper.GetWorkAreaForPoint(cursor);
        var dpi = VisualTreeHelper.GetDpi(this);
        var screenRightDip = screen.Right / dpi.DpiScaleX;
        var screenBottomDip = screen.Bottom / dpi.DpiScaleY;

        var stackOffset = _openCount * (DesiredSize.Height + 10);
        Left = screenRightDip - DesiredSize.Width - 12;
        Top = screenBottomDip - DesiredSize.Height - 12 - stackOffset;

        _openCount++;
        Show();

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
