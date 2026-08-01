using System.IO;
using System.Text.Json;

namespace ClaudeUsageTray;

public sealed class ClaudeProvider : IUsageProvider
{
    public string Name => "Claude";
    public string HomeUrl => "https://claude.ai/settings/usage";
    public string LoginUrl => "https://claude.ai/login";
    public string ProfileFolderName => "WebView2_Claude";

    private static readonly string CreditsDebugFile = Path.Combine(Paths.LogsDir, "credits_debug.txt");

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

            creditsLine = balance is null
                ? Strings.F("provider.claude.credits.used", used, currency)
                : Strings.F("provider.claude.credits.used_of", used, balance, currency);
        }

        return new UsageSnapshot
        {
            ServiceName = Name,
            Ok = true,
            Bars =
            {
                new UsageBar { Label = Strings.T("provider.claude.5h"), Percent = fiveHour, ResetAt = fiveHourReset },
                new UsageBar { Label = Strings.T("provider.weekly"), Percent = sevenDayPct, ResetAt = weeklyReset, IsPrimary = true },
            },
            ExtraLine = creditsLine,
        };
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
