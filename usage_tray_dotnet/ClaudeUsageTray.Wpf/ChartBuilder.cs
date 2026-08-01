using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClaudeUsageTray;

/// <summary>
/// Builds a smoothed (Catmull-Rom) 0-100% line chart over time — shared by
/// the Stats window and, later, the Telegram /stats image, so both render
/// from the exact same code instead of two independent implementations
/// drifting apart.
/// </summary>
internal static class ChartBuilder
{
    public static FrameworkElement Build(
        IReadOnlyList<UsageHistoryPoint> points,
        double width, double height,
        Brush lineBrush, Brush fillBrush, Brush gridBrush, Brush textBrush,
        string emptyMessage)
    {
        var host = new Grid { Width = width, Height = height };

        if (points.Count < 2)
        {
            host.Children.Add(new TextBlock
            {
                Text = emptyMessage,
                Foreground = textBrush,
                Opacity = 0.7,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = width - 32,
            });
            return host;
        }

        const double padTop = 8, padBottom = 20, padLeft = 4, padRight = 4;
        var plotWidth = width - padLeft - padRight;
        var plotHeight = height - padTop - padBottom;

        foreach (var pct in new[] { 0, 50, 100 })
        {
            var y = padTop + plotHeight * (1 - pct / 100.0);
            host.Children.Add(new Line
            {
                X1 = padLeft, X2 = width - padRight, Y1 = y, Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 3 },
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            });
        }

        var minTime = points[0].RecordedAt;
        var maxTime = points[^1].RecordedAt;
        var spanSeconds = Math.Max(1, (maxTime - minTime).TotalSeconds);

        var screenPoints = points.Select(p =>
        {
            var x = padLeft + plotWidth * ((p.RecordedAt - minTime).TotalSeconds / spanSeconds);
            var y = padTop + plotHeight * (1 - Math.Clamp(p.Percent, 0, 100) / 100.0);
            return new Point(x, y);
        }).ToList();

        var linePath = BuildSmoothPath(screenPoints);

        var fillFigure = linePath.Figures[0].Clone();
        fillFigure.Segments.Add(new LineSegment(new Point(screenPoints[^1].X, padTop + plotHeight), true));
        fillFigure.Segments.Add(new LineSegment(new Point(screenPoints[0].X, padTop + plotHeight), true));
        fillFigure.IsClosed = true;
        host.Children.Add(new Path { Data = new PathGeometry(new[] { fillFigure }), Fill = fillBrush });

        host.Children.Add(new Path
        {
            Data = linePath,
            Stroke = lineBrush,
            StrokeThickness = 2.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });

        host.Children.Add(new TextBlock
        {
            Text = minTime.ToLocalTime().ToString("d MMM HH:mm"),
            Foreground = textBrush, Opacity = 0.6, FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
        });
        host.Children.Add(new TextBlock
        {
            Text = maxTime.ToLocalTime().ToString("d MMM HH:mm"),
            Foreground = textBrush, Opacity = 0.6, FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
        });

        AddHoverReadout(host, screenPoints, points, width, lineBrush, textBrush);

        return host;
    }

    /// <summary>
    /// A Grid with no Background set only hit-tests its children's actual
    /// drawn pixels — the empty space between/around the thin line
    /// wouldn't register mouse moves at all without an explicit (if
    /// invisible) Background. Only wired for the live Stats window; on the
    /// Telegram image path nothing ever generates real mouse input, so
    /// this is harmless dead weight there.
    /// </summary>
    private static void AddHoverReadout(Grid host, List<Point> screenPoints, IReadOnlyList<UsageHistoryPoint> points, double width, Brush lineBrush, Brush textBrush)
    {
        host.Background = Brushes.Transparent;

        var hoverDot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = lineBrush,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        var hoverLabel = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = textBrush,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        host.Children.Add(hoverDot);
        host.Children.Add(hoverLabel);

        host.MouseMove += (s, e) =>
        {
            var pos = e.GetPosition(host);
            var nearestIndex = 0;
            var nearestDist = double.MaxValue;
            for (var i = 0; i < screenPoints.Count; i++)
            {
                var dist = Math.Abs(screenPoints[i].X - pos.X);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestIndex = i;
                }
            }

            var sp = screenPoints[nearestIndex];
            var point = points[nearestIndex];

            hoverDot.Margin = new Thickness(sp.X - hoverDot.Width / 2, sp.Y - hoverDot.Height / 2, 0, 0);
            hoverDot.Visibility = Visibility.Visible;

            hoverLabel.Text = $"{point.Percent}%";
            hoverLabel.Margin = new Thickness(Math.Clamp(sp.X - 16, 0, width - 32), Math.Max(0, sp.Y - 18), 0, 0);
            hoverLabel.Visibility = Visibility.Visible;
        };
        host.MouseLeave += (s, e) =>
        {
            hoverDot.Visibility = Visibility.Collapsed;
            hoverLabel.Visibility = Visibility.Collapsed;
        };
    }

    /// <summary>
    /// One header (icon + service name) and chart per service, stacked —
    /// the shared content both the Stats window and the Telegram /stats
    /// image build from, so the two never quietly drift apart.
    /// </summary>
    public static StackPanel BuildServiceBlocks(
        IReadOnlyList<string> serviceNames, UsageHistoryStore historyStore, DateTimeOffset since,
        double chartWidth, double chartHeight,
        Brush textPrimary, Brush textSecondary, Brush lineBrush, Brush fillBrush, Brush gridBrush)
    {
        var container = new StackPanel();
        for (var i = 0; i < serviceNames.Count; i++)
        {
            var serviceName = serviceNames[i];
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, i == serviceNames.Count - 1 ? 0 : 20) };

            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var icon = ServiceIcons.Build(serviceName, 16, textPrimary);
            icon.Margin = new Thickness(0, 0, 8, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(icon);
            header.Children.Add(new TextBlock { Text = serviceName, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = textPrimary, VerticalAlignment = VerticalAlignment.Center });
            block.Children.Add(header);

            var points = historyStore.GetHistory(serviceName, since);
            var chart = Build(points, chartWidth, chartHeight, lineBrush, fillBrush, gridBrush, textSecondary, Strings.T("stats.empty"));
            block.Children.Add(chart);

            container.Children.Add(block);
        }
        return container;
    }

    /// <summary>Catmull-Rom spline through the points, converted to cubic Bezier segments — smooth without overshooting past 0/100%.</summary>
    private static PathGeometry BuildSmoothPath(List<Point> points)
    {
        var figure = new PathFigure { StartPoint = points[0] };
        for (var i = 0; i < points.Count - 1; i++)
        {
            var p0 = i == 0 ? points[i] : points[i - 1];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = i + 2 < points.Count ? points[i + 2] : p2;

            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);

            figure.Segments.Add(new BezierSegment(c1, c2, p2, true));
        }
        return new PathGeometry(new[] { figure });
    }
}
