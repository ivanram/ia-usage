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

        var (fiveHourWindow, weeklyWindow) = FindWindows(usage);
        var bars = new List<UsageBar>();
        if (fiveHourWindow is JsonElement fiveHour)
        {
            bars.Add(BuildWindowBar(fiveHour, Strings.T("provider.claude.5h"), isPrimary: false, qualifier: Strings.T("qualifier.5h"), shortPrefix: Strings.T("prefix.5h")));
        }
        if (weeklyWindow is JsonElement weekly)
        {
            bars.Add(BuildWindowBar(weekly, Strings.T("provider.weekly"), isPrimary: true, qualifier: Strings.T("qualifier.weekly"), shortPrefix: Strings.T("prefix.weekly")));
        }

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
        try { File.AppendAllText(UsageDebugFile, $"{DateTime.Now:O} {usage.GetRawText()}\n"); } catch { /* best effort */ }
    }

    /// <summary>
    /// A plain ChatGPT Plus account's top-level "rate_limit" carries exactly
    /// two windows: primary_window is the 5h one, secondary_window is the
    /// 7-day one. A Codex-enabled Pro account breaks that assumption — its
    /// top-level rate_limit has only ONE window (a 7-day one, sitting under
    /// the "primary_window" key despite not being the 5h window the key name
    /// implies) and secondary_window is null. The real 5h+7day pair the user
    /// actually cares about lives nested under
    /// additional_rate_limits[].rate_limit instead (one entry per model,
    /// e.g. "GPT-5.3-Codex-Spark"). So windows are classified by their
    /// actual limit_window_seconds rather than trusted by key name, and a
    /// source (top-level, or one additional_rate_limits entry) that yields a
    /// complete 5h+weekly pair is preferred over mixing windows from
    /// different sources, so both bars always describe the same quota
    /// bucket. Confirmed against a real Pro/Codex account's raw payload
    /// (chatgpt_usage_debug.txt) after the top-level-only assumption left
    /// that account with a "5-hour limit" that was actually its weekly one,
    /// and no weekly bar at all.
    /// </summary>
    private static (JsonElement? fiveHour, JsonElement? weekly) FindWindows(JsonElement usage)
    {
        var sources = new List<JsonElement>();
        if (usage.TryGetProperty("rate_limit", out var topRateLimit) && topRateLimit.ValueKind == JsonValueKind.Object)
        {
            sources.Add(topRateLimit);
        }
        if (usage.TryGetProperty("additional_rate_limits", out var additional) && additional.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in additional.EnumerateArray())
            {
                if (entry.TryGetProperty("rate_limit", out var entryRateLimit) && entryRateLimit.ValueKind == JsonValueKind.Object)
                {
                    sources.Add(entryRateLimit);
                }
            }
        }

        JsonElement? fallbackFiveHour = null;
        JsonElement? fallbackWeekly = null;

        foreach (var source in sources)
        {
            var (fiveHour, weekly) = ClassifyWindows(source);
            if (fiveHour is not null && weekly is not null)
            {
                return (fiveHour, weekly);
            }

            fallbackFiveHour ??= fiveHour;
            fallbackWeekly ??= weekly;
        }

        return (fallbackFiveHour, fallbackWeekly);
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
