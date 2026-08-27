using System.IO;
using System.Text.Json;

namespace ClaudeUsageTray;

public sealed class ClaudeProvider : IUsageProvider
{
    public string Name => "Claude";
    public string HomeUrl => "https://claude.ai/settings/usage";
    public string LoginUrl => "https://claude.ai/login";
    public string ProfileFolderName => "WebView2_Claude";

    /// <summary>
    /// History/stats service key for the Fable bar (see <see cref="UsageBar.IsFable"/>)
    /// — a distinct series from "Claude" itself, so Fable's own weekly quota
    /// gets its own chart in Stats instead of being folded into or
    /// overwriting Claude's aggregate weekly history.
    /// </summary>
    public const string FableServiceName = "Claude - Fable";

    private static readonly string CreditsDebugFile = Path.Combine(Paths.LogsDir, "credits_debug.txt");
    // Same "log the raw shape so the right field can be found instead of
    // guessed blind" approach that credits_debug.txt was for originally —
    // this is what confirmed the real shape of the Fable-specific weekly
    // quota (see FindModelLimit) after the first guess at it missed. Left
    // in place as a general diagnostic for whatever's next.
    private static readonly string UsageDebugFile = Path.Combine(Paths.LogsDir, "usage_debug.txt");

    private const string KickoffScript = """
        window.__claudeUsageResult = null;
        (async () => {
          try {
            const orgsResp = await fetch('https://claude.ai/api/organizations', {credentials:'include'});
            if (!orgsResp.ok) { window.__claudeUsageResult = JSON.stringify({error:'orgs_http_' + orgsResp.status}); return; }
            const orgs = await orgsResp.json();
            if (!orgs || !orgs.length) { window.__claudeUsageResult = JSON.stringify({error:'no_orgs'}); return; }
            const org = orgs[0].uuid;
            const usageResp = await fetch(`https://claude.ai/api/organizations/${org}/usage`, {credentials:'include'});
            if (!usageResp.ok) { window.__claudeUsageResult = JSON.stringify({error:'usage_http_' + usageResp.status}); return; }
            const usage = await usageResp.json();

            let credits = null;
            try {
              const creditsResp = await fetch(`https://claude.ai/api/organizations/${org}/prepaid/credits`, {credentials:'include'});
              if (creditsResp.ok) credits = await creditsResp.json();
            } catch (e) { /* optional: the "usado / saldo actual" line just won't show */ }

            window.__claudeUsageResult = JSON.stringify({ok:true, usage, credits});
          } catch (e) {
            window.__claudeUsageResult = JSON.stringify({error:String(e)});
          }
        })();
        """;

    private const string PollExpression = "window.__claudeUsageResult";

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
        var fiveHourObj = usage.GetProperty("five_hour");
        var fiveHour = fiveHourObj.GetProperty("utilization").GetInt32();
        var sevenDay = usage.GetProperty("seven_day");
        var sevenDayPct = sevenDay.GetProperty("utilization").GetInt32();

        DateTimeOffset? fiveHourReset = null;
        if (fiveHourObj.TryGetProperty("resets_at", out var fiveHourResetsAt) && fiveHourResetsAt.ValueKind == JsonValueKind.String)
        {
            fiveHourReset = DateTimeOffset.Parse(fiveHourResetsAt.GetString()!).ToLocalTime();
        }

        DateTimeOffset? weeklyReset = null;
        if (sevenDay.TryGetProperty("resets_at", out var resetsAt) && resetsAt.ValueKind == JsonValueKind.String)
        {
            weeklyReset = DateTimeOffset.Parse(resetsAt.GetString()!).ToLocalTime();
        }

        // credits_debug.txt logs the raw prepaid/credits payload every fetch
        // — the previous attempt at this line summed each tranche's
        // granted_amount_minor_units (lifetime granted) instead of what's
        // left of it, showing e.g. "4.42 / 85.00" when the real remaining
        // balance was 39.16. This tries each tranche's own remaining/balance
        // field instead; if that field turns out not to exist either, the
        // log is there to find the right one without more guessing.
        string? creditsLine = null;
        if (usage.TryGetProperty("extra_usage", out var extra) && extra.ValueKind == JsonValueKind.Object
            && extra.TryGetProperty("used_credits", out var usedCredits) && usedCredits.ValueKind == JsonValueKind.Number)
        {
            var decimals = extra.TryGetProperty("decimal_places", out var dp) ? dp.GetInt32() : 2;
            var currency = extra.TryGetProperty("currency", out var cur) ? cur.GetString() ?? "" : "";
            var used = usedCredits.GetDecimal() / (decimal)Math.Pow(10, decimals);
            var divisor = (decimal)Math.Pow(10, decimals);

            decimal? balance = null;
            if (root.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object)
            {
                LogCredits(credits);
                var remaining = SumRemainingMinorUnits(credits, "tranches") + SumRemainingMinorUnits(credits, "promo_tranches");
                if (remaining > 0) balance = remaining / divisor;
            }

            // Once the last tranche hits zero, balance stays null forever
            // (used_credits itself never resets) — showing "used: X" with no
            // balance at that point isn't "you have credits" info any more,
            // it's a permanent leftover of a topped-up account that's now
            // exhausted. Hide the line entirely rather than have it outlive
            // its usefulness.
            creditsLine = balance is null
                ? null
                : Strings.F("provider.claude.credits.used_of", used, balance, currency);
        }

        LogUsageShape(usage);

        var bars = new List<UsageBar>
        {
            new() { Label = Strings.T("provider.claude.5h"), Percent = fiveHour, ResetAt = fiveHourReset, Qualifier = Strings.T("qualifier.5h"), ShortPrefix = Strings.T("prefix.5h") },
            new() { Label = Strings.T("provider.weekly"), Percent = sevenDayPct, ResetAt = weeklyReset, IsPrimary = true, Qualifier = Strings.T("qualifier.weekly"), ShortPrefix = Strings.T("prefix.weekly") },
        };

        // Model-specific weekly quota (Max-plan accounts only, "Fable" model
        // shown separately from the aggregate "All models" bar above) — not
        // every account has this, so it's only added when actually present.
        // Confirmed via usage_debug.txt (see FindModelLimit): it does NOT
        // live as a sibling key of five_hour/seven_day like the first cut of
        // this feature assumed — every one of those top-level keys is an
        // opaque codename (nimbus_quill, cinder_cove, ...) that doesn't
        // reveal which model it's for. The real per-model entry is inside
        // usage.limits[], shaped like {"kind":"weekly_scoped", "percent":5,
        // "resets_at":"...", "scope":{"model":{"display_name":"Fable"}}}.
        if (FindModelLimit(usage, "fable") is { } fable)
        {
            bars.Add(new UsageBar { Label = Strings.T("provider.claude.fable"), Percent = fable.percent, ResetAt = fable.reset, IsFable = true, Qualifier = Strings.T("qualifier.fable"), ShortPrefix = Strings.T("prefix.fable") });
        }

        return new UsageSnapshot
        {
            ServiceName = Name,
            Ok = true,
            Bars = bars,
            ExtraLine = creditsLine,
        };
    }

    /// <summary>
    /// Scans usage.limits[] for the entry scoped to a specific model (e.g.
    /// {"kind":"weekly_scoped","percent":5,"resets_at":"...",
    /// "scope":{"model":{"display_name":"Fable"}}}) — that's where Anthropic
    /// actually puts a model-specific quota, NOT as a sibling key of
    /// five_hour/seven_day the way an earlier version of this guessed
    /// (every one of those top-level keys turned out to be an opaque
    /// codename that says nothing about which model it's for). Matches on
    /// scope.model.display_name rather than "kind" or array position, since
    /// those look more likely to vary across accounts/plans than the
    /// human-readable model name does.
    /// </summary>
    private static (int percent, DateTimeOffset? reset)? FindModelLimit(JsonElement usage, string modelName)
    {
        if (!usage.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array) return null;
        foreach (var limit in limits.EnumerateArray())
        {
            if (!limit.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object) continue;
            if (!scope.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object) continue;
            if (!model.TryGetProperty("display_name", out var displayName) || displayName.ValueKind != JsonValueKind.String) continue;
            if (!displayName.GetString()!.Contains(modelName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!limit.TryGetProperty("percent", out var percentEl) || percentEl.ValueKind != JsonValueKind.Number) continue;

            DateTimeOffset? reset = limit.TryGetProperty("resets_at", out var resetsAt) && resetsAt.ValueKind == JsonValueKind.String
                ? DateTimeOffset.Parse(resetsAt.GetString()!).ToLocalTime()
                : null;
            return (percentEl.GetInt32(), reset);
        }
        return null;
    }

    private static void LogUsageShape(JsonElement usage)
    {
        try { File.AppendAllText(UsageDebugFile, $"{DateTime.Now:O} {usage.GetRawText()}\n"); } catch { /* best effort */ }
    }

    /// <summary>
    /// Tries a few plausible field names for "what's left of this tranche"
    /// rather than "granted_amount_minor_units" (what it started with) —
    /// returns 0, not a wrong number, if none of them are present, so an
    /// unrecognized shape just falls back to used-only instead of showing
    /// something misleading.
    /// </summary>
    private static decimal SumRemainingMinorUnits(JsonElement credits, string arrayName)
    {
        if (!credits.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array) return 0;
        decimal sum = 0;
        foreach (var item in arr.EnumerateArray())
        {
            if (TryGetNumber(item, "remaining_amount_minor_units", out var remaining)
                || TryGetNumber(item, "balance_minor_units", out remaining)
                || TryGetNumber(item, "available_amount_minor_units", out remaining))
            {
                sum += remaining;
            }
        }
        return sum;
    }

    private static bool TryGetNumber(JsonElement obj, string propertyName, out decimal value)
    {
        if (obj.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            value = prop.GetDecimal();
            return true;
        }
        value = 0;
        return false;
    }

    private static void LogCredits(JsonElement credits)
    {
        try { File.AppendAllText(CreditsDebugFile, $"{DateTime.Now:O} {credits.GetRawText()}\n"); } catch { /* best effort */ }
    }
}
