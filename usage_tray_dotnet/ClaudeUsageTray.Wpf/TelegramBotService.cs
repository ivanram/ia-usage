using System.IO;
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
    private CancellationTokenSource? _cts;

    public TelegramBotService(Func<IEnumerable<UsageSnapshot>> getSnapshots, UsageHistoryStore historyStore)
    {
        _getSnapshots = getSnapshots;
        _historyStore = historyStore;
    }

    public void Start(string token, long? boundChatId, Action<long> onBound)
    {
        Stop();
        var client = new TelegramBotClient(token);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _ = client.SetMyCommands(new[]
        {
            new BotCommand { Command = "uso", Description = "Ver uso de Claude y ChatGPT" },
            new BotCommand { Command = "stats", Description = "Ver gráfico de uso reciente" },
        }, cancellationToken: ct);

        client.StartReceiving(
            async (bot, update, innerCt) =>
            {
                if (update.Message is not { Text: { } text } message) return;
                var chatId = message.Chat.Id;

                if (boundChatId is null)
                {
                    boundChatId = chatId;
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
                    await bot.SendMessage(chatId, BuildReply(_getSnapshots()), parseMode: ParseMode.Markdown, replyMarkup: Keyboard, cancellationToken: innerCt);
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

                await bot.SendMessage(chatId, Strings.F("telegrambot.usebutton", UsageButtonText), replyMarkup: Keyboard, cancellationToken: innerCt);
            },
            (bot, exception, innerCt) => Task.CompletedTask,
            new ReceiverOptions { AllowedUpdates = new[] { UpdateType.Message } },
            ct);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
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
}
