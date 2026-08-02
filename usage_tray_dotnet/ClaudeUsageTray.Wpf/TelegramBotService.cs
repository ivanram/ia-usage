using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace ClaudeUsageTray;

/// <summary>
/// Runs a long-polling Telegram bot that replies to /uso (or the "📊 Uso"
/// button) from the bound chat with the current cached usage snapshot, and
/// to /stats with the same usage-history chart the desktop Stats window
/// shows. The first person to message the bot becomes the bound owner;
/// every other chat is silently ignored, so nobody else who finds the bot
/// can query it.
/// </summary>
public sealed class TelegramBotService
{
    private const string UsageButtonText = "📊 Uso";
    private const double StatsImageWidth = 480;
    private const double StatsChartHeight = 130;

    private static readonly ReplyKeyboardMarkup Keyboard = new(new[] { new KeyboardButton(UsageButtonText) })
    {
        ResizeKeyboard = true,
    };

    private readonly Func<IEnumerable<UsageSnapshot>> _getSnapshots;
    private readonly UsageHistoryStore _historyStore;
    private readonly PromptCountStore _promptCountStore;
    private CancellationTokenSource? _cts;

    // Kept as instance state (rather than locals captured by the receive
    // loop's closures, as before) specifically so SendNotificationAsync can
    // push a message from outside any incoming-message handler — the
    // reset/exhausted/80% notifications originate from TrayOrchestrator's
    // own refresh cycle, not from anything the user sent the bot.
    private TelegramBotClient? _client;
    private long? _boundChatId;

    // Shuffle-bag for /cita-del-dia: indices are drawn without replacement
    // until empty, then refilled — so all 100 show up before any repeats,
    // resetting on app restart rather than being persisted (a joke command
    // doesn't need a database).
    private readonly List<int> _remainingQuoteIndices = new();
    private readonly Random _quoteRandom = new();

    public TelegramBotService(Func<IEnumerable<UsageSnapshot>> getSnapshots, UsageHistoryStore historyStore, PromptCountStore promptCountStore)
    {
        _getSnapshots = getSnapshots;
        _historyStore = historyStore;
        _promptCountStore = promptCountStore;
    }

    public void Start(string token, long? boundChatId, Action<long> onBound)
    {
        Stop();
        var client = new TelegramBotClient(token);
        _client = client;
        _boundChatId = boundChatId;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _ = client.SetMyCommands(new[]
        {
            new BotCommand { Command = "uso", Description = "Ver uso de las IA" },
            new BotCommand { Command = "stats", Description = "Ver gráfico de uso reciente" },
            new BotCommand { Command = "apps", Description = "Ver apps en uso en el PC" },
            new BotCommand { Command = "proyectos", Description = "Ver proyectos recientes de Claude Code y ChatGPT Codex" },
            new BotCommand { Command = "apagarpc", Description = "Apagar el PC (pide confirmación)" },
            new BotCommand { Command = "reiniciar", Description = "Reiniciar el PC (pide confirmación)" },
            // Telegram command names can't contain hyphens — "cita_del_dia"
            // is the menu-visible form; the handler below also matches the
            // literal "/cita-del-dia" text for anyone who types it that way.
            new BotCommand { Command = "cita_del_dia", Description = "Recibe una cita random del día" },
        }, cancellationToken: ct);

        client.StartReceiving(
            async (bot, update, innerCt) =>
            {
                if (update.CallbackQuery is { } callback)
                {
                    await HandlePowerCallbackAsync(bot, callback, innerCt);
                    return;
                }

                if (update.Message is not { Text: { } text } message) return;
                var chatId = message.Chat.Id;

                if (boundChatId is null)
                {
                    boundChatId = chatId;
                    _boundChatId = chatId;
                    onBound(chatId);
                    await bot.SendMessage(chatId,
                        "Vinculado. Pulsa el botón o escribe /uso cuando quieras ver tu consumo de Claude/ChatGPT.",
                        replyMarkup: Keyboard, cancellationToken: innerCt);
                    return;
                }

                if (chatId != boundChatId) return; // Someone else found the bot: ignore silently.

                var normalized = text.Trim();
                if (normalized == "/start")
                {
                    await bot.SendMessage(chatId, Strings.T("telegrambot.prompt"), replyMarkup: Keyboard, cancellationToken: innerCt);
                    return;
                }

                if (normalized == "/uso" || normalized.StartsWith("/uso@") || normalized == UsageButtonText)
                {
                    var snapshots = _getSnapshots().ToList();
                    var image = BuildUsageImage(snapshots);
                    if (image is null)
                    {
                        await bot.SendMessage(chatId, BuildReply(snapshots), parseMode: ParseMode.Markdown, replyMarkup: Keyboard, cancellationToken: innerCt);
                    }
                    else
                    {
                        using var stream = new MemoryStream(image);
                        await bot.SendPhoto(chatId, InputFile.FromStream(stream, "uso.png"),
                            caption: BuildReply(snapshots), parseMode: ParseMode.Markdown,
                            replyMarkup: Keyboard, cancellationToken: innerCt);
                    }
                    return;
                }

                if (normalized == "/stats" || normalized.StartsWith("/stats@"))
                {
                    var snapshots = _getSnapshots().Where(s => s.Ok).ToList();
                    var image = BuildStatsImage(snapshots.Select(s => s.ServiceName).ToList());
                    if (image is null)
                    {
                        await bot.SendMessage(chatId, Strings.T("stats.noservices"), replyMarkup: Keyboard, cancellationToken: innerCt);
                    }
                    else
                    {
                        using var stream = new MemoryStream(image);
                        await bot.SendPhoto(chatId, InputFile.FromStream(stream, "stats.png"),
                            caption: BuildReply(snapshots), parseMode: ParseMode.Markdown,
                            replyMarkup: Keyboard, cancellationToken: innerCt);
                    }
                    return;
                }

                if (normalized == "/apps" || normalized.StartsWith("/apps@"))
                {
                    var apps = await RunningAppsHelper.GetRunningAppsAsync();
                    await bot.SendMessage(chatId, BuildAppsReply(apps), parseMode: ParseMode.Markdown, replyMarkup: Keyboard, cancellationToken: innerCt);
                    return;
                }

                if (normalized == "/proyectos" || normalized.StartsWith("/proyectos@"))
                {
                    var tasks = ClaudeCodeProjectsHelper.GetRecentTasks()
                        .Concat(CodexProjectsHelper.GetRecentTasks())
                        .ToList();
                    // Latest totals only — refreshed at most every 30 min by
                    // TrayOrchestrator's sampling timer, not a live re-scan
                    // (see ClaudeCodeProjectsHelper/CodexProjectsHelper's
                    // GetPromptCountsByProject doc comment for why).
                    var promptTotals = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Claude Code"] = _promptCountStore.GetLatestTotalsByProject("Claude Code"),
                        ["Codex"] = _promptCountStore.GetLatestTotalsByProject("Codex"),
                    };
                    await bot.SendMessage(chatId, BuildProjectsReply(tasks, promptTotals), parseMode: ParseMode.Markdown, replyMarkup: Keyboard, cancellationToken: innerCt);
                    return;
                }

                if (normalized == "/apagarpc" || normalized.StartsWith("/apagarpc@"))
                {
                    await bot.SendMessage(chatId, Strings.T("telegrambot.shutdown.confirm"), parseMode: ParseMode.Markdown,
                        replyMarkup: BuildConfirmKeyboard("shutdown"), cancellationToken: innerCt);
                    return;
                }

                if (normalized == "/reiniciar" || normalized.StartsWith("/reiniciar@"))
                {
                    await bot.SendMessage(chatId, Strings.T("telegrambot.restart.confirm"), parseMode: ParseMode.Markdown,
                        replyMarkup: BuildConfirmKeyboard("restart"), cancellationToken: innerCt);
                    return;
                }

                if (normalized == "/cita-del-dia" || normalized.StartsWith("/cita-del-dia@")
                    || normalized == "/cita_del_dia" || normalized.StartsWith("/cita_del_dia@"))
                {
                    var quote = GetNextQuote();
                    await bot.SendMessage(chatId, $"📜 _{EscapeMarkdown(quote)}_", parseMode: ParseMode.Markdown,
                        replyMarkup: Keyboard, cancellationToken: innerCt);
                    return;
                }

                await bot.SendMessage(chatId, Strings.F("telegrambot.usebutton", UsageButtonText), replyMarkup: Keyboard, cancellationToken: innerCt);
            },
            (bot, exception, innerCt) => Task.CompletedTask,
            new ReceiverOptions { AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery } },
            ct);
    }

    private static InlineKeyboardMarkup BuildConfirmKeyboard(string action) => new(new[]
    {
        new[]
        {
            InlineKeyboardButton.WithCallbackData(Strings.T("telegrambot.confirm.yes"), $"{action}:yes"),
            InlineKeyboardButton.WithCallbackData(Strings.T("telegrambot.confirm.no"), $"{action}:no"),
        },
    });

    /// <summary>
    /// Handles the Sí/No tap on the /apagarpc and /reiniciar confirmation
    /// prompts. The Telegram-side confirmation IS the safety gate the user
    /// asked for — this only fires the actual shutdown/restart once they've
    /// explicitly tapped "Sí" on a message only their own bound chat can see.
    /// </summary>
    private async Task HandlePowerCallbackAsync(ITelegramBotClient bot, CallbackQuery callback, CancellationToken ct)
    {
        var chatId = callback.Message?.Chat.Id;
        if (chatId is null || chatId != _boundChatId || callback.Data is not { } data)
        {
            await bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
            return;
        }

        await bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);

        var parts = data.Split(':');
        if (parts.Length != 2) return;
        var action = parts[0];
        var confirmed = parts[1] == "yes";
        var messageId = callback.Message!.MessageId;

        if (!confirmed)
        {
            await bot.EditMessageText(chatId.Value, messageId, Strings.T("telegrambot.cancelled"), cancellationToken: ct);
            return;
        }

        var (doingKey, shutdownArg) = action switch
        {
            "shutdown" => ("telegrambot.shutdown.doing", "/s /t 5"),
            "restart" => ("telegrambot.restart.doing", "/r /t 5"),
            _ => (null, null),
        };
        if (doingKey is null) return;

        await bot.EditMessageText(chatId.Value, messageId, Strings.T(doingKey), cancellationToken: ct);

        // A short native delay (not a way to undo the confirmation, which
        // already happened) — mostly so the "Apagando…" edit above has a
        // moment to actually reach Telegram before the connection drops.
        Process.Start(new ProcessStartInfo("shutdown", shutdownArg!) { UseShellExecute = true, CreateNoWindow = true });
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _client = null;
    }

    /// <summary>
    /// Fire-and-forget push for the reset/exhausted/80% notifications —
    /// best effort, since a Telegram hiccup here should never affect the
    /// desktop toast that already fired alongside it.
    /// </summary>
    public async Task SendNotificationAsync(string message)
    {
        if (_client is null || _boundChatId is null) return;
        try
        {
            await _client.SendMessage(_boundChatId.Value, message);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>
    /// Renders the same history chart the desktop Stats window shows, as a
    /// PNG for sendPhoto. Building/measuring/rendering WPF visuals only
    /// works on a Dispatcher (UI) thread — the Telegram polling loop calls
    /// this from its own thread, so the actual work is marshalled onto the
    /// app's single UI dispatcher. Colors are fixed rather than pulled from
    /// the live app theme: this image is standalone content viewed inside
    /// Telegram, not part of the app's own window chrome, and most Telegram
    /// clients default to a dark theme anyway.
    /// </summary>
    private byte[]? BuildStatsImage(List<string> serviceNames)
    {
        if (serviceNames.Count == 0) return null;

        return Application.Current.Dispatcher.Invoke(() =>
        {
            var textPrimary = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0xF2, 0xF2));
            var textSecondary = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xAD, 0xAD, 0xAD));
            var accent = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7C, 0x97, 0xE0));
            var fillBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7C, 0x97, 0xE0)) { Opacity = 0.18 };
            var gridBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x58));
            var background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x1E, 0x22));

            var title = new TextBlock
            {
                Text = $"📊 {Strings.T("stats.title")}",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = textPrimary,
                Margin = new Thickness(0, 0, 0, 18),
            };

            var since = DateTimeOffset.UtcNow.AddHours(-24);
            var blocks = ChartBuilder.BuildServiceBlocks(serviceNames, _historyStore, since,
                StatsImageWidth - 48, StatsChartHeight, textPrimary, textSecondary, accent, fillBrush, gridBrush);

            var content = new StackPanel();
            content.Children.Add(title);
            content.Children.Add(blocks);

            var root = new Border { Background = background, Padding = new Thickness(24), Child = content };
            root.Measure(new Size(StatsImageWidth, double.PositiveInfinity));
            var height = root.DesiredSize.Height;
            root.Arrange(new Rect(0, 0, StatsImageWidth, height));
            root.UpdateLayout();

            var rtb = new RenderTargetBitmap((int)StatsImageWidth, (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
            rtb.Render(root);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        });
    }

    /// <summary>
    /// Renders each service's real app icon (the same ServiceIcons used
    /// everywhere else) plus its bars as a PNG, instead of the plain-text
    /// reply's flat colored circle standing in for a service — Telegram
    /// text messages can't embed arbitrary custom icons inline, only
    /// Unicode emoji, so an actual rendered image is the only way to show
    /// the real Claude/ChatGPT/Grok marks here. Skips services that
    /// errored (no bars to draw) — the text caption still covers those.
    /// </summary>
    private byte[]? BuildUsageImage(List<UsageSnapshot> snapshots)
    {
        var okSnapshots = snapshots.Where(s => s.Ok && s.Bars.Count > 0).ToList();
        if (okSnapshots.Count == 0) return null;

        return Application.Current.Dispatcher.Invoke(() =>
        {
            const double imageWidth = 420;
            var textPrimary = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0xF2, 0xF2));
            var textSecondary = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xAD, 0xAD, 0xAD));
            var background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x1E, 0x22));
            var trackBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x3E));

            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = $"🤖 {Strings.T("app.name")}",
                FontSize = 19,
                FontWeight = FontWeights.Bold,
                Foreground = textPrimary,
                Margin = new Thickness(0, 0, 0, 18),
            });

            var barAreaWidth = imageWidth - 48;
            for (var i = 0; i < okSnapshots.Count; i++)
            {
                var snap = okSnapshots[i];
                var block = new StackPanel { Margin = new Thickness(0, 0, 0, i == okSnapshots.Count - 1 ? 0 : 18) };

                var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
                var icon = ServiceIcons.Build(snap.ServiceName, 18, textPrimary);
                icon.Margin = new Thickness(0, 0, 8, 0);
                icon.VerticalAlignment = VerticalAlignment.Center;
                header.Children.Add(icon);
                header.Children.Add(new TextBlock { Text = snap.ServiceName, FontSize = 15, FontWeight = FontWeights.Medium, Foreground = textPrimary, VerticalAlignment = VerticalAlignment.Center });
                block.Children.Add(header);

                foreach (var bar in snap.Bars)
                {
                    var labelRow = new Grid { Margin = new Thickness(0, 0, 0, 5) };
                    labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var label = new TextBlock { Text = bar.Label, FontSize = 12, Foreground = textSecondary };
                    var pct = new TextBlock { Text = $"{bar.Percent}%", FontSize = 12, FontWeight = FontWeights.Medium, Foreground = textPrimary };
                    Grid.SetColumn(pct, 1);
                    labelRow.Children.Add(label);
                    labelRow.Children.Add(pct);
                    block.Children.Add(labelRow);

                    var barGrid = new Grid { Height = 8, Margin = new Thickness(0, 0, 0, 14) };
                    barGrid.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = trackBrush });
                    barGrid.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Background = ChartBuilder.GradientForPercent(bar.Percent),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Width = Math.Clamp(bar.Percent, 0, 100) / 100.0 * barAreaWidth,
                    });
                    block.Children.Add(barGrid);
                }

                content.Children.Add(block);
            }

            var root = new Border { Background = background, Padding = new Thickness(24), Child = content };
            root.Measure(new Size(imageWidth, double.PositiveInfinity));
            var height = root.DesiredSize.Height;
            root.Arrange(new Rect(0, 0, imageWidth, height));
            root.UpdateLayout();

            var rtb = new RenderTargetBitmap((int)imageWidth, (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
            rtb.Render(root);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        });
    }

    private static readonly Dictionary<string, string> ServiceEmoji = new()
    {
        ["Claude"] = "🟠",
        ["ChatGPT"] = "🟢",
    };

    private static string BuildReply(IEnumerable<UsageSnapshot> snapshots)
    {
        var blocks = snapshots.Select(snap =>
        {
            var emoji = ServiceEmoji.TryGetValue(snap.ServiceName, out var e) ? e : "🤖";
            var header = $"{emoji} *{snap.ServiceName}*";

            if (!snap.Ok) return $"{header}\n{snap.ErrorMessage ?? Strings.T("telegrambot.nodata")}";

            var lines = snap.Bars.Select(b =>
            {
                var bar = AsciiBar(b.Percent);
                var reset = b.ResetAt is { } r ? $" · {TimeFormat.RelativeShort(r)}" : "";
                return $"{b.Label}\n{bar} {b.Percent}%{reset}";
            });
            var text = $"{header}\n" + string.Join("\n\n", lines);
            return string.IsNullOrEmpty(snap.ExtraLine) ? text : $"{text}\n\n{snap.ExtraLine}";
        }).ToList();

        return blocks.Count == 0 ? Strings.T("telegrambot.none") : string.Join("\n\n━━━━━━━━━━━━━\n\n", blocks);
    }

    private static string AsciiBar(int percent, int slots = 10)
    {
        var filled = Math.Clamp((int)Math.Round(percent / 100.0 * slots), 0, slots);
        return "[" + new string('█', filled) + new string('░', slots - filled) + "]";
    }

    // Capped rather than sent in full — a very cluttered desktop could
    // otherwise turn this into an unreadably long message.
    private const int MaxAppsListed = 20;

    /// <summary>
    /// ▶️ marks whichever app is in the foreground right now; 🟢 marks any
    /// other app whose CPU usage crossed the "active" threshold during the
    /// sampling window (something churning in the background); 🤖 calls out
    /// anything that looks like an AI app/tab by name, foreground or not.
    /// </summary>
    private static string BuildAppsReply(List<RunningAppsHelper.RunningApp> apps)
    {
        if (apps.Count == 0) return Strings.T("telegrambot.apps.none");

        var lines = apps.Take(MaxAppsListed).Select(app =>
        {
            var marker = app.IsForeground ? "▶️" : app.IsActive ? "🟢" : "⚪";
            var aiTag = RunningAppsHelper.IsAiApp(app) ? " 🤖" : "";
            var status = app.IsForeground
                ? Strings.T("telegrambot.apps.foreground")
                : app.IsActive ? Strings.F("telegrambot.apps.active", (int)Math.Round(app.CpuPercent)) : null;
            var suffix = status is null ? "" : $" — {status}";
            return $"{marker} *{EscapeMarkdown(app.Name)}*{aiTag}{suffix}";
        });

        var header = $"{Strings.T("telegrambot.apps.title")}\n\n";
        var footer = $"\n\n_{Strings.F("telegrambot.apps.footer", apps.Count)}_";
        return header + string.Join("\n", lines) + footer;
    }

    /// <summary>Legacy Telegram Markdown treats these four characters as formatting — process/window names can contain any of them incidentally.</summary>
    private static string EscapeMarkdown(string text) =>
        text.Replace("_", "\\_").Replace("*", "\\*").Replace("`", "\\`").Replace("[", "\\[");

    // Draws without replacement from DailyQuotes.All until exhausted, then
    // refills — so every quote shows up once before any repeats.
    private string GetNextQuote()
    {
        if (_remainingQuoteIndices.Count == 0)
            _remainingQuoteIndices.AddRange(Enumerable.Range(0, DailyQuotes.All.Length));

        var pick = _quoteRandom.Next(_remainingQuoteIndices.Count);
        var index = _remainingQuoteIndices[pick];
        _remainingQuoteIndices.RemoveAt(pick);
        return DailyQuotes.All[index];
    }

    // Bounds both dimensions of the message — a very active machine with
    // lots of history could otherwise turn this into a wall of text.
    private const int MaxProjectsListed = 8;
    private const int MaxTasksPerProject = 4;

    // Claude Code first (this app started as a Claude-focused tool), then
    // whatever else shows up — an unrecognized future agent still gets its
    // own section, just after these two.
    private static readonly string[] AgentSectionOrder = { "Claude Code", "Codex" };

    /// <summary>
    /// One section per agent, each grouping its own tasks by project: a
    /// header line per project (🟢 + whichever task is active, or 📁 +
    /// however long ago the most recent one touched it, plus its latest
    /// known prompt total when PromptCountStore has one), then an indented
    /// ↳ line per other known chat/task under that same project. Never the
    /// conversation content itself, which neither ClaudeCodeProjectsHelper
    /// nor CodexProjectsHelper ever read — only names, timestamps, and
    /// prompt counts.
    /// </summary>
    private static string BuildProjectsReply(List<AgentTask> tasks, Dictionary<string, Dictionary<string, int>> promptTotalsByAgent)
    {
        if (tasks.Count == 0) return Strings.T("telegrambot.projects.none");

        var agentSections = tasks
            .GroupBy(t => t.Agent)
            .OrderBy(g => Array.IndexOf(AgentSectionOrder, g.Key) is var idx && idx >= 0 ? idx : int.MaxValue)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(agentGroup =>
            {
                var promptTotals = promptTotalsByAgent.TryGetValue(agentGroup.Key, out var pt) ? pt : new Dictionary<string, int>();

                var projectGroups = agentGroup
                    .GroupBy(t => t.ProjectPath, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(t => t.LastActivity).ToList())
                    .OrderByDescending(g => g[0].IsActiveNow)
                    .ThenByDescending(g => g[0].LastActivity)
                    .Take(MaxProjectsListed);

                var blocks = projectGroups.Select(g =>
                {
                    var head = g[0];
                    var projectName = EscapeMarkdown(System.IO.Path.GetFileName(head.ProjectPath.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : head.ProjectPath);
                    var marker = head.IsActiveNow ? "🟢" : "📁";
                    var headLine = $"{marker} *{projectName}*";
                    var statusLine = head.IsActiveNow ? Strings.T("telegrambot.projects.active") : TimeFormat.Ago(head.LastActivity);

                    var promptLine = promptTotals.TryGetValue(head.ProjectPath, out var promptCount) && promptCount > 0
                        ? Strings.F("telegrambot.projects.prompts", promptCount)
                        : null;

                    // Every task gets its own ↳ line, including the most
                    // recent one — it used to be folded into the header
                    // line instead, which made a multi-task project
                    // silently drop its most recent task from the list.
                    // Exception: a lone task with no name (Claude Code only
                    // ever names the currently-active session, never a
                    // historical one) would just repeat the status line's
                    // own "hace X" — skip it rather than show the same
                    // information twice.
                    var skipTaskLines = g.Count == 1 && string.IsNullOrWhiteSpace(g[0].Name);
                    var taskLines = skipTaskLines
                        ? Enumerable.Empty<string>()
                        : g.Take(MaxTasksPerProject).Select(t =>
                        {
                            var label = !string.IsNullOrWhiteSpace(t.Name) ? EscapeMarkdown(t.Name!) : TimeFormat.Ago(t.LastActivity);
                            var activeTag = t.IsActiveNow ? "🟢 " : "";
                            return $" ↳ {activeTag}{label}";
                        });

                    var lines = new List<string> { headLine, statusLine };
                    if (promptLine is not null) lines.Add(promptLine);
                    lines.AddRange(taskLines);
                    return string.Join("\n", lines);
                });

                return $"🗂️ *{EscapeMarkdown(AgentDisplayNames.For(agentGroup.Key))}*\n\n" + string.Join("\n\n", blocks);
            });

        return string.Join("\n\n", agentSections);
    }
}
