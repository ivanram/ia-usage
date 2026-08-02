using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClaudeUsageTray;

/// <summary>
/// A hand-rolled replacement for the tray icon's right-click menu — see the
/// XAML file's comment for why this exists instead of a real ContextMenu.
/// The caller (TrayOrchestrator) owns the actual menu content: it clears
/// and re-adds rows each time before showing, the same way PopupWindow's
/// content is rebuilt on each Render() rather than templated in XAML.
/// </summary>
public partial class TrayMenuWindow : Window
{
    private Brush _textPrimary = Brushes.Black;
    private Brush _textSecondary = Brushes.Gray;
    private Brush _hoverBrush = new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80));

    public TrayMenuWindow()
    {
        InitializeComponent();
    }

    /// <summary>Same off-screen Show()+Hide() warm-up as PopupWindow — see its own WarmUp() doc comment for why.</summary>
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

    private void OnDeactivated(object? sender, EventArgs e) => Hide();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Hide();
    }

    public void ApplyTheme(bool isDark)
    {
        RootBorder.Background = isDark
            ? new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2E))
            : new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
        _textPrimary = isDark
            ? new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2))
            : new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
        _textSecondary = isDark
            ? new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xB8))
            : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
    }

    public void ClearItems() => ContentHost.Children.Clear();

    public void AddItem(string glyph, string text, Action onClick, bool indented = false)
    {
        ContentHost.Children.Add(BuildRow(glyph, text, onClick, indented));
    }

    public void AddLabel(string text)
    {
        ContentHost.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = _textSecondary,
            Margin = new Thickness(12, 8, 12, 2),
        });
    }

    public void AddSeparator()
    {
        ContentHost.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(8, 6, 8, 6),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80)),
        });
    }

    private FrameworkElement BuildRow(string glyph, string text, Action onClick, bool indented)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (!string.IsNullOrEmpty(glyph))
        {
            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = _textSecondary,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);
        }

        var label = new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = _textPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(indented ? 30 : 12, 7, 12, 7),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = row,
        };
        border.MouseEnter += (s, e) => border.Background = _hoverBrush;
        border.MouseLeave += (s, e) => border.Background = Brushes.Transparent;
        border.MouseLeftButtonUp += (s, e) =>
        {
            Hide();
            onClick();
        };

        return border;
    }

    /// <summary>
    /// Positions like a native context menu would: anchored near the
    /// cursor, opening upward from the taskbar corner, clamped to stay
    /// fully on-screen. Measure() here is reliable because WarmUp() already
    /// took this window through one real Show() earlier — see PopupWindow's
    /// own ShowNearCursor for the fuller explanation of why a truly
    /// never-shown window's Measure() can't be trusted the same way.
    /// </summary>
    public void ShowNearCursor()
    {
        UpdateLayout();
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = DesiredSize;

        var cursor = NativeScreenHelper.GetCursorPosition();
        var screen = NativeScreenHelper.GetWorkAreaForPoint(cursor);
        var dpi = VisualTreeHelper.GetDpi(this);

        var cursorXDip = cursor.X / dpi.DpiScaleX;
        var screenRightDip = screen.Right / dpi.DpiScaleX;
        var screenLeftDip = screen.Left / dpi.DpiScaleX;
        var screenTopDip = screen.Top / dpi.DpiScaleY;
        var screenBottomDip = screen.Bottom / dpi.DpiScaleY;

        Left = Math.Max(screenLeftDip + 8, Math.Min(cursorXDip, screenRightDip - desired.Width - 8));
        Top = Math.Max(screenTopDip + 8, screenBottomDip - desired.Height - 8);

        Show();
        Activate();
    }
}
