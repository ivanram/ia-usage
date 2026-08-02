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

        // padLeft leaves room for the "0%/50%/100%" axis labels; padBottom
        // for a row of hourly time labels under the plot; padTop for a
        // compact start-date label above it (hours are covered by the
        // per-gridline labels below, so only the date needs calling out once).
        const double padTop = 18, padBottom = 16, padLeft = 30, padRight = 6;
        var plotWidth = Math.Max(1, width - padLeft - padRight);
        var plotHeight = Math.Max(1, height - padTop - padBottom);

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
            host.Children.Add(new TextBlock
            {
                Text = $"{pct}%",
                Foreground = textBrush, Opacity = 0.65, FontSize = 9,
                Width = padLeft - 4, TextAlignment = TextAlignment.Right,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, y - 6, 0, 0),
            });
        }

        var minTime = points[0].RecordedAt;
        var maxTime = points[^1].RecordedAt;
        var spanSeconds = Math.Max(1, (maxTime - minTime).TotalSeconds);

        host.Children.Add(new TextBlock
        {
            Text = minTime.ToLocalTime().ToString("d MMM"),
            Foreground = textBrush, Opacity = 0.55, FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(padLeft, 0, 0, 0),
        });

        // One dashed vertical line per hour boundary crossed, each labeled
        // with its clock time — but only when there's enough pixel room
        // since the previously drawn label, so dense data doesn't turn into
        // an unreadable smear of overlapping text.
        const double minLabelSpacing = 32;
        var lastLabelX = double.NegativeInfinity;
        var localMin = minTime.ToLocalTime();
        var hourCursor = new DateTimeOffset(localMin.Year, localMin.Month, localMin.Day, localMin.Hour, 0, 0, localMin.Offset).AddHours(1);
        while (hourCursor < maxTime)
        {
            var x = padLeft + plotWidth * ((hourCursor - minTime).TotalSeconds / spanSeconds);
            host.Children.Add(new Line
            {
                X1 = x, X2 = x, Y1 = padTop, Y2 = padTop + plotHeight,
                Stroke = gridBrush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 3 },
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            });

            if (x - lastLabelX >= minLabelSpacing && x <= width - padRight - 16)
            {
                host.Children.Add(new TextBlock
                {
                    Text = hourCursor.ToString("HH:mm"),
                    Foreground = textBrush, Opacity = 0.6, FontSize = 9,
                    Width = 30, TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(x - 15, padTop + plotHeight + 3, 0, 0),
                });
                lastLabelX = x;
            }

            hourCursor = hourCursor.AddHours(1);
        }

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
    /// One header (icon + service name) and chart per service, stacked
    /// vertically — used both by the desktop Stats window (a timeline reads
    /// naturally growing downward) and the Telegram /stats image (which is
    /// just scrolled like any other photo message).
    ///
    /// <paramref name="viewportWidth"/> and <paramref name="onZoom"/> are
    /// only used by the interactive Stats window: when set, each chart is
    /// built at <paramref name="chartWidth"/> (which the caller may have
    /// already scaled up by its own zoom factor) but wrapped in a
    /// horizontally-scrollable viewport fixed at <paramref name="viewportWidth"/>,
    /// with mouse-wheel-over-chart reporting a zoom-in/out direction back to
    /// the caller instead of scrolling. Telegram's image rendering leaves
    /// both null and gets the old plain, unwrapped chart.
    /// </summary>
    public static StackPanel BuildServiceBlocks(
        IReadOnlyList<string> serviceNames, UsageHistoryStore historyStore, DateTimeOffset since,
        double chartWidth, double chartHeight,
        Brush textPrimary, Brush textSecondary, Brush lineBrush, Brush fillBrush, Brush gridBrush,
        double? viewportWidth = null, Action<int>? onZoom = null)
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

            if (viewportWidth is { } vw && onZoom is not null)
            {
                var scroller = new ScrollViewer
                {
                    Width = vw,
                    Height = chartHeight,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = chart,
                };
                scroller.PreviewMouseWheel += (s, e) =>
                {
                    onZoom(Math.Sign(e.Delta));
                    e.Handled = true;
                };
                block.Children.Add(scroller);
            }
            else
            {
                block.Children.Add(chart);
            }

            container.Children.Add(block);
        }
        return container;
    }

    /// <summary>
    /// Same green/amber/red-by-percent gradient the popup panel's own bars
    /// use — shared so the Telegram usage image looks like the same app,
    /// not a reinvented palette.
    /// </summary>
    public static Brush GradientForPercent(int percent)
    {
        var (from, to) = percent switch
        {
            < 60 => ("#72D08F", "#3F9E63"),
            < 85 => ("#F3C36A", "#D99420"),
            _ => ("#EE8484", "#CE3D3D"),
        };
        return new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(from),
            (Color)ColorConverter.ConvertFromString(to),
            new Point(0, 0), new Point(1, 0));
    }

    /// <summary>
    /// Catmull-Rom spline through the points, converted to cubic Bezier
    /// segments — smooth without overshooting past 0/100%. Plain Catmull-Rom
    /// tangents are sized from the NEIGHBORING segments too, so a sharp real
    /// jump right next to a flat run (a big usage jump followed by several
    /// flat readings, say) produced a visible hump/dip that didn't
    /// correspond to any actual data point — clamping each control point's
    /// Y to its own segment's endpoint range keeps the curve from swinging
    /// past the two values it's actually connecting.
    /// </summary>
    private static PathGeometry BuildSmoothPath(List<Point> points)
    {
        var figure = new PathFigure { StartPoint = points[0] };
        for (var i = 0; i < points.Count - 1; i++)
        {
            var p0 = i == 0 ? points[i] : points[i - 1];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = i + 2 < points.Count ? points[i + 2] : p2;

            var segMinY = Math.Min(p1.Y, p2.Y);
            var segMaxY = Math.Max(p1.Y, p2.Y);

            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, Math.Clamp(p1.Y + (p2.Y - p0.Y) / 6, segMinY, segMaxY));
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, Math.Clamp(p2.Y - (p3.Y - p1.Y) / 6, segMinY, segMaxY));

            figure.Segments.Add(new BezierSegment(c1, c2, p2, true));
        }
        return new PathGeometry(new[] { figure });
    }
}
