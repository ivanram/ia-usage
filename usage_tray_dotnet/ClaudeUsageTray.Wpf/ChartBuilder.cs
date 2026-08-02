using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    /// <summary>
    /// <paramref name="Element"/> is what actually goes in the chart's
    /// (possibly zoomable/scrollable) viewport — either just the main chart
    /// Grid, or that Grid stacked with a separate prompts bar sub-panel
    /// below it. <paramref name="UsageGroup"/>/<paramref name="PromptGroup"/>
    /// are the toggle targets for the clickable legend built by
    /// <see cref="BuildServiceBlocks"/> — each wraps everything belonging to
    /// that one series so a single Visibility flip hides/shows all of it.
    /// <paramref name="PromptGroup"/> is null when there was no prompt data
    /// to plot at all, telling the caller to skip the legend entirely.
    /// <paramref name="ExtraHeight"/> is how much taller <paramref name="Element"/>
    /// is than the requested chart height (0 unless a bars sub-panel got
    /// added) — a fixed-height container around it (the desktop window's
    /// zoom/scroll viewport) needs to account for this itself, or the bars
    /// end up silently clipped off since that container never resizes to
    /// its content on its own.
    /// </summary>
    public readonly record struct ChartBuildResult(FrameworkElement Element, UIElement UsageGroup, UIElement? PromptGroup, double ExtraHeight);

    public static ChartBuildResult Build(
        IReadOnlyList<UsageHistoryPoint> points,
        double width, double height,
        Brush lineBrush, Brush fillBrush, Brush gridBrush, Brush textBrush,
        string emptyMessage,
        IReadOnlyList<DateTimeOffset>? resetTimestamps = null,
        IReadOnlyList<(DateTimeOffset At, int Delta)>? promptSeries = null,
        Brush? promptLineBrush = null,
        bool promptsAsBars = false)
    {
        var host = new Grid { Width = width, Height = height };
        var usageGroup = new Grid();
        host.Children.Add(usageGroup);

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
            return new ChartBuildResult(host, usageGroup, null, 0);
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
        usageGroup.Children.Add(new Path { Data = new PathGeometry(new[] { fillFigure }), Fill = fillBrush });

        usageGroup.Children.Add(new Path
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

                usageGroup.Children.Add(new TextBlock
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

        // Prompt series in range, independent of how it ends up drawn (line
        // overlay for "Totales", bars sub-panel for "Nuevos") — also feeds
        // the hover readout's "· N prompts" suffix either way.
        var relevantPrompts = promptSeries is { Count: > 0 }
            ? promptSeries.Where(p => p.At >= minTime && p.At <= maxTime).ToList()
            : new List<(DateTimeOffset At, int Delta)>();
        var promptHoverPoints = relevantPrompts.Select(p =>
            (new Point(padLeft + plotWidth * ((p.At - minTime).TotalSeconds / spanSeconds), 0.0), p.Delta)).ToList();

        FrameworkElement element = host;
        UIElement? promptGroup = null;

        // "Totales" (cumulative) stays an overlaid line on the same canvas
        // as the usage chart, so a jump in usage can be visually correlated
        // with the prompt volume that caused it. "Nuevos" (per-interval
        // deltas) reads much better as vertical bars — but bars drawn on
        // top of the usage fill would constantly clash with it, so those go
        // in a separate strip below instead (see BuildPromptBarsPanel).
        if (relevantPrompts.Count > 0 && promptLineBrush is not null)
        {
            if (promptsAsBars)
            {
                var barsPanel = BuildPromptBarsPanel(relevantPrompts, width, PromptBarsPanelHeight, promptLineBrush, minTime, spanSeconds, padLeft, padRight);
                var stack = new StackPanel();
                stack.Children.Add(host);
                stack.Children.Add(barsPanel);
                element = stack;
                promptGroup = barsPanel;
            }
            else
            {
                var lineGroup = new Grid();
                host.Children.Add(lineGroup);
                promptGroup = lineGroup;

                var promptMax = Math.Max(1, relevantPrompts.Max(p => p.Delta));
                var promptScreenPoints = relevantPrompts.Select(p =>
                {
                    var x = padLeft + plotWidth * ((p.At - minTime).TotalSeconds / spanSeconds);
                    var y = padTop + plotHeight * (1 - Math.Clamp(p.Delta / (double)promptMax, 0, 1));
                    return new Point(x, y);
                }).ToList();

                if (promptScreenPoints.Count >= 2)
                {
                    lineGroup.Children.Add(new Path
                    {
                        Data = BuildLinePath(promptScreenPoints),
                        Stroke = promptLineBrush,
                        StrokeThickness = 1.75,
                        StrokeDashArray = new DoubleCollection { 4, 2 },
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                    });
                }
                foreach (var screen in promptScreenPoints)
                {
                    lineGroup.Children.Add(new Ellipse
                    {
                        Width = 5, Height = 5,
                        Fill = promptLineBrush,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(screen.X - 2.5, screen.Y - 2.5, 0, 0),
                        IsHitTestVisible = false,
                    });
                }

                lineGroup.Children.Add(new TextBlock
                {
                    Text = Strings.F("stats.prompts.legend", promptMax),
                    Foreground = promptLineBrush, Opacity = 0.85, FontSize = 9, FontWeight = FontWeights.Medium,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 0, padRight, 0),
                });
            }
        }

        AddHoverReadout(host, screenPoints, points, width, lineBrush, textBrush, resetMarkers, promptHoverPoints);

        var extraHeight = ReferenceEquals(element, host) ? 0 : PromptBarsPanelHeight;
        return new ChartBuildResult(element, usageGroup, promptGroup, extraHeight);
    }

    // Height of the small bars strip drawn below the main chart for the
    // "Nuevos" prompt view — enough to read bar heights at a glance without
    // competing for space with the usage chart above it.
    private const double PromptBarsPanelHeight = 34;

    /// <summary>
    /// A compact "volume pane" style strip, same width/x-axis mapping as
    /// the main chart above it (padLeft/padRight/plotWidth line up exactly)
    /// so each bar sits under its corresponding moment in time — structurally
    /// separate from the usage chart's own canvas specifically so bars can
    /// never visually overlap the usage line/fill, no matter how tall a
    /// spike gets.
    /// </summary>
    private static FrameworkElement BuildPromptBarsPanel(
        List<(DateTimeOffset At, int Delta)> series, double width, double height,
        Brush barBrush, DateTimeOffset minTime, double spanSeconds, double padLeft, double padRight)
    {
        var panel = new Grid { Width = width, Height = height };
        var plotWidth = Math.Max(1, width - padLeft - padRight);
        var max = Math.Max(1, series.Max(p => p.Delta));

        // Leaves headroom at the top for the "(máx N)" label so a full-height
        // bar never sits directly under it.
        const double topPad = 12;
        var barAreaHeight = height - topPad;
        var barWidth = Math.Clamp(plotWidth / Math.Max(1, series.Count) * 0.6, 2, 10);

        foreach (var (at, delta) in series)
        {
            var x = padLeft + plotWidth * ((at - minTime).TotalSeconds / spanSeconds);
            var barHeight = Math.Max(1.5, barAreaHeight * Math.Clamp(delta / (double)max, 0, 1));
            panel.Children.Add(new Rectangle
            {
                Width = barWidth,
                Height = barHeight,
                Fill = barBrush,
                RadiusX = 1, RadiusY = 1,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(x - barWidth / 2, height - barHeight, 0, 0),
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = Strings.F("stats.prompts.legend", max),
            Foreground = barBrush, Opacity = 0.85, FontSize = 9, FontWeight = FontWeights.Medium,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, padRight, 0),
        });

        return panel;
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
        PromptCountStore? promptCountStore = null, Brush? promptLineBrush = null, bool useTotalPrompts = false)
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
                promptSeries = useTotalPrompts
                    ? promptCountStore.GetAgentTotalSeries(agent, since).Select(p => (p.At, p.Total)).ToList()
                    : promptCountStore.GetAgentDeltaSeries(agent, since);
            }
            // "Totales" reads naturally as a running line (see Build's own
            // reasoning); "Nuevos" per-interval deltas read naturally as
            // bars instead, and — critically — bars drawn ON the usage
            // chart would constantly fight it for space, so that mode moves
            // to a separate strip below (promptsAsBars: true).
            var chart = Build(points, chartWidth, chartHeight, lineBrush, fillBrush, gridBrush, textSecondary, Strings.T("stats.empty"), resets, promptSeries, promptLineBrush, promptsAsBars: !useTotalPrompts);

            if (viewportWidth is { } vw && onZoom is not null)
            {
                var scroller = new ScrollViewer
                {
                    Width = vw,
                    // Fixed-height + VerticalScrollBarVisibility=Disabled means
                    // anything taller than this is silently clipped, not just
                    // hidden behind a scrollbar — has to include the bars
                    // sub-panel's own height or it never appears at all.
                    Height = chartHeight + chart.ExtraHeight,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = chart.Element,
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
                block.Children.Add(chart.Element);
            }

            // Only worth showing when there's actually a second series to
            // distinguish from the usage line — a service with no coding
            // agent data (Grok, or simply no prompts yet) just gets the
            // plain chart, same as before this existed.
            if (chart.PromptGroup is not null)
            {
                block.Children.Add(BuildChartLegend(lineBrush, promptLineBrush!, chart.UsageGroup, chart.PromptGroup, textSecondary));
            }

            container.Children.Add(block);
        }
        return container;
    }

    /// <summary>
    /// Two clickable chips under a chart — clicking one toggles that
    /// series' Visibility on the actual chart elements Build() exposed via
    /// <see cref="ChartBuildResult"/>, so this needs no chart-rebuild or
    /// external state to work. Doubles as a plain (inert but still
    /// legible) legend on the static Telegram image, since nothing there
    /// ever generates a click.
    /// </summary>
    private static FrameworkElement BuildChartLegend(Brush usageBrush, Brush promptBrush, UIElement usageTarget, UIElement promptTarget, Brush textSecondary)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(BuildLegendChip(Strings.T("stats.legend.usage"), usageBrush, usageTarget, textSecondary));
        row.Children.Add(BuildLegendChip(Strings.T("stats.legend.prompts"), promptBrush, promptTarget, textSecondary));
        return row;
    }

    private static FrameworkElement BuildLegendChip(string label, Brush dotColor, UIElement target, Brush textSecondary)
    {
        var dot = new Ellipse { Width = 8, Height = 8, Fill = dotColor, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { Text = label, FontSize = 10, Foreground = textSecondary, VerticalAlignment = VerticalAlignment.Center };
        var chip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 14, 0),
            Cursor = Cursors.Hand,
        };
        chip.Children.Add(dot);
        chip.Children.Add(text);
        chip.MouseLeftButtonUp += (s, e) =>
        {
            var isVisible = target.Visibility != Visibility.Collapsed;
            target.Visibility = isVisible ? Visibility.Collapsed : Visibility.Visible;
            var fadedOpacity = isVisible ? 0.35 : 1.0;
            dot.Opacity = fadedOpacity;
            text.Opacity = fadedOpacity;
        };
        return chip;
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
