using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace ClaudeUsageTray;

/// <summary>
/// Runs a long-polling Telegram bot that replies to /uso (or the "📊 Uso"
/// button) from the bound chat with the current cached usage snapshot.
/// The first person to message the bot becomes the bound owner; every other
/// chat is silently ignored, so nobody else who finds the bot can query it.
/// </summary>
public sealed class TelegramBotService
{
    private const string UsageButtonText = "📊 Uso";

    private static readonly ReplyKeyboardMarkup Keyboard = new(new[] { new KeyboardButton(UsageButtonText) })
    {
        ResizeKeyboard = true,
    };

    private readonly Func<IEnumerable<UsageSnapshot>> _getSnapshots;
    private CancellationTokenSource? _cts;

    public TelegramBotService(Func<IEnumerable<UsageSnapshot>> getSnapshots)
    {
        _getSnapshots = getSnapshots;
    }

    public void Start(string token, long? boundChatId, Action<long> onBound)
    {
        Stop();
        var client = new TelegramBotClient(token);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _ = client.SetMyCommands(new[] { new BotCommand { Command = "uso", Description = "Ver uso de Claude y ChatGPT" } }, cancellationToken: ct);

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
