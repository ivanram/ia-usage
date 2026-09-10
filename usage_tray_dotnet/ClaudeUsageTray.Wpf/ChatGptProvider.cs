using System.Globalization;
using System.IO;
using System.Text.Json;

namespace ClaudeUsageTray;

public sealed class ChatGptProvider : IUsageProvider
{
    public string Name => "ChatGPT";
    public string HomeUrl => "https://chatgpt.com/#settings/Usage";
    public string LoginUrl => "https://chatgpt.com/auth/login";
    public string ProfileFolderName => "WebView2_ChatGPT";

    // Same "log the raw shape instead of guessing blind" approach as
    // ClaudeProvider's usage_debug.txt — some accounts (e.g. ones with a
    // Codex-specific per-model rate limit alongside the general one) don't
    // expose secondary_window at the top level the way a plain Plus account
    // does, so the weekly bar silently disappears. This file is how we find
    // where the weekly window actually lives for those accounts.
    private static readonly string UsageDebugFile = Path.Combine(Paths.LogsDir, "chatgpt_usage_debug.txt");

    private const string KickoffScript = """
        window.__chatgptUsageResult = null;
        (async () => {
          try {
            const sessionResp = await fetch('https://chatgpt.com/api/auth/session', {credentials:'include'});
            if (!sessionResp.ok) { window.__chatgptUsageResult = JSON.stringify({error:'session_http_' + sessionResp.status}); return; }
            const session = await sessionResp.json();
            const token = session.accessToken;
            if (!token) { window.__chatgptUsageResult = JSON.stringify({error:'no_token'}); return; }
            const usageResp = await fetch('https://chatgpt.com/backend-api/wham/usage', {
              credentials:'include',
              headers: { 'Authorization': `Bearer ${token}` }
            });
            if (!usageResp.ok) { window.__chatgptUsageResult = JSON.stringify({error:'usage_http_' + usageResp.status}); return; }
            const usage = await usageResp.json();
            window.__chatgptUsageResult = JSON.stringify({ok:true, usage});
          } catch (e) {
            window.__chatgptUsageResult = JSON.stringify({error:String(e)});
          }
        })();
        """;

    private const string PollExpression = "window.__chatgptUsageResult";

    public async Task<UsageSnapshot> FetchAsync(WebViewUsageHost host)
    {
        var inner = await host.RunScriptForResultAsync(KickoffScript, PollExpression);
        if (inner is null)
        {
            return new UsageSnapshot { ServiceName = Name, Ok = false, ErrorMessage = Strings.T("provider.timeout") };
        }

        using var doc = JsonDocument.Parse(inner);
        var root = doc.RootElement;
        if (!root.TryGetProperty("ok", out _) || root.TryGetProperty("error", out _))
        {
            return new UsageSnapshot { ServiceName = Name, Ok = false, ErrorMessage = inner };
        }

        var usage = root.GetProperty("usage");
        LogUsageShape(usage);

        var bars = BuildBars(usage);

        string? creditsLine = null;
        if (usage.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object
            && credits.TryGetProperty("has_credits", out var hasCredits) && hasCredits.ValueKind == JsonValueKind.True
            && credits.TryGetProperty("balance", out var balance)
            && decimal.TryParse(balance.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var balanceValue))
        {
            creditsLine = Strings.F("provider.chatgpt.credits", Math.Floor(balanceValue));
        }

        // rate_limit_reset_credits are the free "reset my quota now" grants ChatGPT
        // occasionally hands out (e.g. after an outage) — available_count is how many
        // are still unused. We deliberately drop the expiry date here to keep this
        // line short; the ChatGPT settings page has the full detail if needed.
        if (usage.TryGetProperty("rate_limit_reset_credits", out var resetCredits) && resetCredits.ValueKind == JsonValueKind.Object
            && resetCredits.TryGetProperty("available_count", out var availableCount) && availableCount.ValueKind == JsonValueKind.Number
            && availableCount.GetInt32() > 0)
        {
            var resetsLine = Strings.F("provider.chatgpt.resets", availableCount.GetInt32());
            creditsLine = creditsLine is null ? resetsLine : $"{creditsLine} · {resetsLine}";
        }

        return new UsageSnapshot
        {
            ServiceName = Name,
            Ok = true,
            Bars = bars,
            ExtraLine = creditsLine,
        };
    }

    private static void LogUsageShape(JsonElement usage)
    {
        if (!AppSettings.DiagnosticsEnabled) return;
        try { File.AppendAllText(UsageDebugFile, $"{DateTime.Now:O} {usage.GetRawText()}\n"); } catch { /* best effort */ }
    }

    /// <summary>
    /// A plain ChatGPT Plus account's top-level "rate_limit" carries exactly
    /// two windows: primary_window is the 5h one, secondary_window is the
    /// 7-day one — that account gets the usual 2 bars. A Codex-enabled Pro
    /// account breaks that assumption in a way that turned out to need a
    /// THIRD bar, not just a relabeled pair: its top-level rate_limit has
    /// only ONE window (a 7-day one, sitting under the "primary_window" key
    /// despite not being the 5h window the key name implies) and
    /// secondary_window is null — but that lone top-level window is a real,
    /// separate "general" weekly quota, not a stand-in for the model's own.
    /// The model actually used (e.g. "GPT-5.3-Codex-Spark") has its own
    /// complete 5h+7day pair nested under additional_rate_limits[].rate_limit,
    /// with its own independent reset schedule. So an affected account shows
    /// three numbers that all move separately: 5h (model), weekly (general),
    /// weekly (model) — confirmed against a real Pro/Codex account's raw
    /// payload (chatgpt_usage_debug.txt) after a first fix that only showed
    /// the model's pair silently dropped the general weekly bar entirely.
    /// Windows are classified by their actual limit_window_seconds rather
    /// than trusted by key name throughout, since the "primary_window" key
    /// lies about which window it is for this account type.
    /// </summary>
    private static List<UsageBar> BuildBars(JsonElement usage)
    {
        var bars = new List<UsageBar>();

        (JsonElement? fiveHour, JsonElement? weekly) top = (null, null);
        if (usage.TryGetProperty("rate_limit", out var topRateLimit) && topRateLimit.ValueKind == JsonValueKind.Object)
        {
            top = ClassifyWindows(topRateLimit);
        }

        JsonElement? modelFiveHour = null;
        JsonElement? modelWeekly = null;
        string? modelName = null;
        if (usage.TryGetProperty("additional_rate_limits", out var additional) && additional.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in additional.EnumerateArray())
            {
                if (!entry.TryGetProperty("rate_limit", out var entryRateLimit) || entryRateLimit.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var (five, weekly) = ClassifyWindows(entryRateLimit);
                if (five is null || weekly is null)
                {
                    continue;
                }
                modelFiveHour = five;
                modelWeekly = weekly;
                modelName = entry.TryGetProperty("limit_name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
                break;
            }
        }

        if (top.fiveHour is not null && top.weekly is not null)
        {
            // Plain account: one bucket covers both windows, same as every
            // other provider in this app.
            bars.Add(BuildWindowBar(top.fiveHour.Value, Strings.T("provider.claude.5h"), isPrimary: false, Strings.T("qualifier.5h"), Strings.T("prefix.5h")));
            bars.Add(BuildWindowBar(top.weekly.Value, Strings.T("provider.weekly"), isPrimary: true, Strings.T("qualifier.weekly"), Strings.T("prefix.weekly")));
        }
        else if (top.weekly is not null && modelFiveHour is not null && modelWeekly is not null)
        {
            // Codex-style account: three independent quotas.
            bars.Add(BuildWindowBar(modelFiveHour.Value, Strings.T("provider.claude.5h"), isPrimary: false, Strings.T("qualifier.5h"), Strings.T("prefix.5h")));
            bars.Add(BuildWindowBar(top.weekly.Value, Strings.T("provider.chatgpt.weekly_general"), isPrimary: true, Strings.T("qualifier.chatgpt.weekly_general"), Strings.T("prefix.weekly")));
            var label = modelName is null ? Strings.T("provider.weekly") : Strings.F("provider.chatgpt.weekly_model", modelName);
            var qualifier = modelName is null ? Strings.T("qualifier.weekly") : Strings.F("qualifier.chatgpt.weekly_model", modelName);
            bars.Add(BuildWindowBar(modelWeekly.Value, label, isPrimary: false, qualifier, Strings.T("prefix.chatgpt.weekly_model")));
        }
        else
        {
            // Unrecognized/partial shape — fall back to whatever's
            // available rather than showing nothing.
            var fiveHour = top.fiveHour ?? modelFiveHour;
            var weekly = top.weekly ?? modelWeekly;
            if (fiveHour is not null) bars.Add(BuildWindowBar(fiveHour.Value, Strings.T("provider.claude.5h"), isPrimary: false, Strings.T("qualifier.5h"), Strings.T("prefix.5h")));
            if (weekly is not null) bars.Add(BuildWindowBar(weekly.Value, Strings.T("provider.weekly"), isPrimary: true, Strings.T("qualifier.weekly"), Strings.T("prefix.weekly")));
        }

        return bars;
    }

    private static (JsonElement? fiveHour, JsonElement? weekly) ClassifyWindows(JsonElement rateLimit)
    {
        JsonElement? fiveHour = null;
        JsonElement? weekly = null;

        foreach (var propertyName in new[] { "primary_window", "secondary_window" })
        {
            if (!rateLimit.TryGetProperty(propertyName, out var window) || window.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            if (!window.TryGetProperty("limit_window_seconds", out var secs) || secs.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            // A day is a safer cutoff than hardcoding 18000/604800 exactly --
            // every window seen so far is either ~5h or ~7d, nothing in between.
            if (secs.GetInt64() <= 86400)
            {
                fiveHour ??= window;
            }
            else
            {
                weekly ??= window;
            }
        }

        return (fiveHour, weekly);
    }

    private static UsageBar BuildWindowBar(JsonElement window, string label, bool isPrimary, string qualifier, string shortPrefix)
    {
        var percent = window.TryGetProperty("used_percent", out var pct) && pct.ValueKind == JsonValueKind.Number
            ? pct.GetInt32()
            : 0;

        DateTimeOffset? resetAtValue = null;
        if (window.TryGetProperty("reset_at", out var resetAt) && resetAt.ValueKind == JsonValueKind.Number)
        {
            resetAtValue = DateTimeOffset.FromUnixTimeSeconds(resetAt.GetInt64()).ToLocalTime();
        }

        return new UsageBar { Label = label, Percent = percent, ResetAt = resetAtValue, IsPrimary = isPrimary, Qualifier = qualifier, ShortPrefix = shortPrefix };
    }
}
