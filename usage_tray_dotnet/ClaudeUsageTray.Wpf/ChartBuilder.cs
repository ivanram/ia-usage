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
        string emptyMessage,
        IReadOnlyList<DateTimeOffset>? resetTimestamps = null,
        IReadOnlyList<(DateTimeOffset At, int Delta)>? promptSeries = null,
        Brush? promptLineBrush = null)
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

        // One dashed vertical line per grid boundary crossed, labeled with
        // clock time for short (Today-ish) ranges or the date itself once
        // the span gets long enough that hourly lines would be an
        // unreadable smear — Week/Month views from the Stats window's own
        // range tabs land in the day/week buckets below. Labels are also
        // skipped when there's not enough pixel room since the last one,
        // for the same reason.
        var totalSpan = maxTime - minTime;
        var (gridInterval, labelFormat, labelWidth) = totalSpan <= TimeSpan.FromHours(30)
            ? (TimeSpan.FromHours(1), "HH:mm", 30.0)
            : totalSpan <= TimeSpan.FromDays(12)
                ? (TimeSpan.FromDays(1), "d MMM", 36.0)
                : (TimeSpan.FromDays(7), "d MMM", 36.0);

        const double minLabelSpacing = 32;
        var lastLabelX = double.NegativeInfinity;
        var localMin = minTime.ToLocalTime();
        var gridCursor = gridInterval < TimeSpan.FromDays(1)
            ? new DateTimeOffset(localMin.Year, localMin.Month, localMin.Day, localMin.Hour, 0, 0, localMin.Offset).Add(gridInterval)
            : new DateTimeOffset(localMin.Year, localMin.Month, localMin.Day, 0, 0, 0, localMin.Offset).Add(gridInterval);
        while (gridCursor < maxTime)
        {
            var x = padLeft + plotWidth * ((gridCursor - minTime).TotalSeconds / spanSeconds);
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
                    Text = gridCursor.ToString(labelFormat),
                    Foreground = textBrush, Opacity = 0.6, FontSize = 9,
                    Width = labelWidth, TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(x - labelWidth / 2, padTop + plotHeight + 3, 0, 0),
                });
                lastLabelX = x;
            }

            gridCursor = gridCursor.Add(gridInterval);
        }

        var screenPoints = points.Select(p =>
        {
            var x = padLeft + plotWidth * ((p.RecordedAt - minTime).TotalSeconds / spanSeconds);
            var y = padTop + plotHeight * (1 - Math.Clamp(p.Percent, 0, 100) / 100.0);
            return new Point(x, y);
        }).ToList();

        var linePath = BuildLinePath(screenPoints);

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

        // ✨ at each detected weekly-reset moment (see UsageHistoryStore.RecordReset)
        // — sat right on the line at that point, same as everything else here,
        // so it's obvious which dip in the line was an actual reset versus
        // just a quiet refresh.
        var resetMarkers = new List<(double X, double Y)>();
        if (resetTimestamps is { Count: > 0 })
        {
            foreach (var resetAt in resetTimestamps)
            {
                if (resetAt < minTime || resetAt > maxTime) continue;
                var x = padLeft + plotWidth * ((resetAt - minTime).TotalSeconds / spanSeconds);
                var y = screenPoints.OrderBy(p => Math.Abs(p.X - x)).First().Y;
                resetMarkers.Add((x, y));

                host.Children.Add(new TextBlock
                {
                    Text = "✨",
                    FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(x - 7, y - 20, 0, 0),
                    IsHitTestVisible = false,
                });
            }
        }

        // Prompt-count overlay — an independently-scaled line (own min/max,
        // not 0-100%) showing how many prompts were made in each sampling
        // interval, straight from PromptCountStore. Overlaid rather than
        // plotted separately so a usage jump can be visually correlated
        // with the prompt volume that caused it. No area fill (this is
        // meant to read as a distinct secondary series, not another region
        // competing with the main one) and dashed so it's unambiguous which
        // line is which even without color.
        var promptScreenPoints = new List<(Point Screen, int Delta)>();
        if (promptSeries is { Count: > 0 } && promptLineBrush is not null)
        {
            var relevant = promptSeries.Where(p => p.At >= minTime && p.At <= maxTime).ToList();
            if (relevant.Count > 0)
            {
                var promptMax = Math.Max(1, relevant.Max(p => p.Delta));
                promptScreenPoints = relevant.Select(p =>
                {
                    var x = padLeft + plotWidth * ((p.At - minTime).TotalSeconds / spanSeconds);
                    var y = padTop + plotHeight * (1 - Math.Clamp(p.Delta / (double)promptMax, 0, 1));
                    return (new Point(x, y), p.Delta);
                }).ToList();

                if (promptScreenPoints.Count >= 2)
                {
                    host.Children.Add(new Path
                    {
                        Data = BuildLinePath(promptScreenPoints.Select(p => p.Screen).ToList()),
                        Stroke = promptLineBrush,
                        StrokeThickness = 1.75,
                        StrokeDashArray = new DoubleCollection { 4, 2 },
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                    });
                }
                foreach (var (screen, _) in promptScreenPoints)
                {
                    host.Children.Add(new Ellipse
                    {
                        Width = 5, Height = 5,
                        Fill = promptLineBrush,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(screen.X - 2.5, screen.Y - 2.5, 0, 0),
                        IsHitTestVisible = false,
                    });
                }

                host.Children.Add(new TextBlock
                {
                    Text = Strings.F("stats.prompts.legend", promptMax),
                    Foreground = promptLineBrush, Opacity = 0.85, FontSize = 9, FontWeight = FontWeights.Medium,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 0, padRight, 0),
                });
            }
        }

        AddHoverReadout(host, screenPoints, points, width, lineBrush, textBrush, resetMarkers, promptScreenPoints);

        return host;
    }

    /// <summary>
    /// A Grid with no Background set only hit-tests its children's actual
    /// drawn pixels — the empty space between/around the thin line
    /// wouldn't register mouse moves at all without an explicit (if
    /// invisible) Background. Only wired for the live Stats window; on the
    /// Telegram image path nothing ever generates real mouse input, so
    /// this is harmless dead weight there. When the cursor lands near a
    /// reset marker's own X, the readout shows "Reseteo" instead of a
    /// percent — a reset is a discrete event, not a reading, so labeling it
    /// as a percent would be misleading.
    /// </summary>
    private static void AddHoverReadout(Grid host, List<Point> screenPoints, IReadOnlyList<UsageHistoryPoint> points, double width, Brush lineBrush, Brush textBrush, List<(double X, double Y)> resetMarkers, List<(Point Screen, int Delta)> promptPoints)
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

        const double resetHoverTolerancePx = 7;

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

            var nearestReset = resetMarkers
                .Where(m => Math.Abs(m.X - pos.X) <= resetHoverTolerancePx)
                .OrderBy(m => Math.Abs(m.X - pos.X))
                .Select(m => ((double X, double Y)?)m)
                .FirstOrDefault();
            var (anchorX, anchorY) = nearestReset ?? (sp.X, sp.Y);

            hoverDot.Margin = new Thickness(anchorX - hoverDot.Width / 2, anchorY - hoverDot.Height / 2, 0, 0);
            hoverDot.Visibility = Visibility.Visible;

            var promptSuffix = "";
            if (promptPoints.Count > 0)
            {
                var nearestPrompt = promptPoints.OrderBy(p => Math.Abs(p.Screen.X - pos.X)).First();
                promptSuffix = $" · {Strings.F("stats.prompts.hover", nearestPrompt.Delta)}";
            }

            hoverLabel.Text = (nearestReset is not null ? Strings.T("stats.reset") : $"{point.Percent}%") + promptSuffix;
            hoverLabel.Margin = new Thickness(Math.Clamp(anchorX - 16, 0, width - 60), Math.Max(0, anchorY - 18), 0, 0);
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
    // "Claude" and "ChatGPT" are the billed-usage services this chart is
    // actually keyed on; the prompt-count overlay comes from the CODING
    // AGENT that drives that usage (Claude Code -> Claude's quota, Codex ->
    // ChatGPT's) — different names for a reason that's obvious once you
    // see the two lines move together. Grok has no local coding-agent
    // counterpart, so it just never gets an overlay.
    private static readonly Dictionary<string, string> CodingAgentByService = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Claude"] = "Claude Code",
        ["ChatGPT"] = "Codex",
    };

    public static StackPanel BuildServiceBlocks(
        IReadOnlyList<string> serviceNames, UsageHistoryStore historyStore, DateTimeOffset since,
        double chartWidth, double chartHeight,
        Brush textPrimary, Brush textSecondary, Brush lineBrush, Brush fillBrush, Brush gridBrush,
        double? viewportWidth = null, Action<int>? onZoom = null,
        PromptCountStore? promptCountStore = null, Brush? promptLineBrush = null)
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
            var resets = historyStore.GetResets(serviceName, since);
            List<(DateTimeOffset At, int Delta)>? promptSeries = null;
            if (promptCountStore is not null && CodingAgentByService.TryGetValue(serviceName, out var agent))
            {
                promptSeries = promptCountStore.GetAgentDeltaSeries(agent, since);
            }
            var chart = Build(points, chartWidth, chartHeight, lineBrush, fillBrush, gridBrush, textSecondary, Strings.T("stats.empty"), resets, promptSeries, promptLineBrush);

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
    /// Plain straight segments between consecutive points. A Catmull-Rom
    /// smoothed spline was tried here (twice), but usage history has wildly
    /// uneven gaps between readings — a burst of samples a minute apart,
    /// then the app closed overnight leaving an 11-hour silent gap before
    /// the next one — and Catmull-Rom sizes its tangents from the
    /// surrounding points' TIME spacing too, not just their values.
    /// Clamping the Y side of that (the previous attempt) didn't stop it:
    /// next to one of these lopsided gaps the tangent's X component
    /// overshoots so far that the curve visibly loops forward and back
    /// before reaching the next real point — a spike that isn't in the
    /// data at all, confirmed by reading the raw history.db rows straight
    /// (perfectly monotonic, no real dip). Straight lines can't produce
    /// that failure mode by construction, at the cost of visible corners
    /// at each reading instead of a smoothed curve.
    /// </summary>
    private static PathGeometry BuildLinePath(List<Point> points)
    {
        var figure = new PathFigure { StartPoint = points[0] };
        for (var i = 1; i < points.Count; i++)
        {
            figure.Segments.Add(new LineSegment(points[i], true));
        }
        return new PathGeometry(new[] { figure });
    }
}
