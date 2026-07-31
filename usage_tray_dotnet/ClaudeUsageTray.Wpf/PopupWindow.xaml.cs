using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClaudeUsageTray;

public partial class PopupWindow : Window
{
    public event EventHandler? RefreshRequested;
    public event EventHandler? SettingsRequested;

    private readonly List<(ProgressBar Bar, double Target)> _animatedBars = new();

    public PopupWindow()
    {
        InitializeComponent();
    }

    private void OnDeactivated(object? sender, EventArgs e) => Hide();

    public void Render(IEnumerable<UsageSnapshot> snapshots)
    {
        ContentHost.Children.Clear();
        _animatedBars.Clear();

        var first = true;
        foreach (var snap in snapshots)
        {
            if (!first) ContentHost.Children.Add(BuildSeparator());
            first = false;

            ContentHost.Children.Add(new TextBlock
            {
                Text = snap.ServiceName,
                FontSize = 16,
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(0, 0, 0, 10),
            });

            if (!snap.Ok)
            {
                var err = new TextBlock
                {
                    Text = snap.ErrorMessage ?? "No se pudo leer el uso",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 280,
                    FontSize = 13,
                };
                err.SetResourceReference(TextBlock.ForegroundProperty, "MaterialDesignValidationErrorBrush");
                ContentHost.Children.Add(err);
                continue;
            }

            foreach (var bar in snap.Bars)
            {
                ContentHost.Children.Add(BuildBarRow(bar));
            }

            if (!string.IsNullOrEmpty(snap.ExtraLine))
            {
                var extra = new TextBlock
                {
                    Text = snap.ExtraLine,
                    FontSize = 12,
                    Margin = new Thickness(0, 10, 0, 0),
                };
                extra.SetResourceReference(TextBlock.ForegroundProperty, "MaterialDesignBodyLight");
                ContentHost.Children.Add(extra);
            }
        }

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        footer.Children.Add(BuildLinkButton("Actualizar", () => RefreshRequested?.Invoke(this, EventArgs.Empty)));
        footer.Children.Add(BuildLinkButton("Ajustes", () => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        ContentHost.Children.Add(footer);
    }

    private Button BuildLinkButton(string text, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 12,
            Margin = new Thickness(12, 0, 0, 0),
            Padding = new Thickness(4),
        };
        button.SetResourceReference(StyleProperty, "MaterialDesignFlatButton");
        button.Click += (s, e) => onClick();
        return button;
    }

    private static Border BuildSeparator() => new()
    {
        Height = 1,
        Margin = new Thickness(0, 4, 0, 14),
        Background = (Brush)new BrushConverter().ConvertFrom("#22808080")!,
    };

    private Grid BuildBarRow(UsageBar bar)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());

        var label = new TextBlock { Text = bar.Label, FontSize = 13 };
        Grid.SetRow(label, 0);
        Grid.SetColumn(label, 0);

        var pct = new TextBlock { Text = $"{bar.Percent}%", FontSize = 13, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(pct, 0);
        Grid.SetColumn(pct, 1);

        var barControl = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 6,
            Margin = new Thickness(0, 6, 0, 2),
            Foreground = ColorForPercent(bar.Percent),
        };
        barControl.SetResourceReference(StyleProperty, "MaterialDesignLinearProgressBar");
        Grid.SetRow(barControl, 1);
        Grid.SetColumnSpan(barControl, 2);
        _animatedBars.Add((barControl, bar.Percent));

        grid.Children.Add(label);
        grid.Children.Add(pct);
        grid.Children.Add(barControl);

        if (bar.ResetAt is { } resetAt)
        {
            var reset = new TextBlock
            {
                Text = $"{TimeFormat.Relative(resetAt)} · {resetAt:ddd dd MMM HH:mm}",
                FontSize = 11,
            };
            reset.SetResourceReference(TextBlock.ForegroundProperty, "MaterialDesignBodyLight");
            Grid.SetRow(reset, 2);
            Grid.SetColumnSpan(reset, 2);
            grid.Children.Add(reset);
        }

        return grid;
    }

    private static Brush ColorForPercent(int percent) => percent switch
    {
        < 60 => (Brush)new BrushConverter().ConvertFrom("#58BD7D")!,
        < 85 => (Brush)new BrushConverter().ConvertFrom("#EBAA3C")!,
        _ => (Brush)new BrushConverter().ConvertFrom("#E05A5A")!,
    };

    public void ShowNearCursor()
    {
        UpdateLayout();

        var cursor = NativeScreenHelper.GetCursorPosition();
        var screen = NativeScreenHelper.GetWorkAreaForPoint(cursor);
        var dpi = VisualTreeHelper.GetDpi(this);

        var widthDip = ActualWidth;
        var heightDip = ActualHeight;
        var cursorXDip = cursor.X / dpi.DpiScaleX;
        var screenRightDip = screen.Right / dpi.DpiScaleX;
        var screenLeftDip = screen.Left / dpi.DpiScaleX;
        var screenBottomDip = screen.Bottom / dpi.DpiScaleY;

        Left = Math.Max(screenLeftDip + 8, Math.Min(cursorXDip, screenRightDip - widthDip - 8));
        Top = screenBottomDip - heightDip - 8;

        Show();
        Activate();
        PlayEntranceAnimation();
    }

    private void PlayEntranceAnimation()
    {
        var storyboard = (Storyboard)RootBorder.Resources["EntranceStoryboard"];
        storyboard.Begin(RootBorder);

        for (var i = 0; i < _animatedBars.Count; i++)
        {
            var (bar, target) = _animatedBars[i];
            var anim = new DoubleAnimation
            {
                From = 0,
                To = target,
                Duration = TimeSpan.FromMilliseconds(500),
                BeginTime = TimeSpan.FromMilliseconds(80 * i),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            bar.BeginAnimation(RangeBase.ValueProperty, anim);
        }
    }
}
