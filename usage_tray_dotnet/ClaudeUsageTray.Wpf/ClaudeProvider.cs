using System.Text.Json;

namespace ClaudeUsageTray;

public sealed class ClaudeProvider : IUsageProvider
{
    public string Name => "Claude";
    public string HomeUrl => "https://claude.ai/settings/usage";
    public string LoginUrl => "https://claude.ai/login";
    public string ProfileFolderName => "WebView2_Claude";

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
            } catch (e) { /* optional: extra-usage total is a nice-to-have, not fetched below if this fails */ }

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

        string? creditsLine = null;
        if (usage.TryGetProperty("extra_usage", out var extra) && extra.ValueKind == JsonValueKind.Object
            && extra.TryGetProperty("used_credits", out var usedCredits) && usedCredits.ValueKind == JsonValueKind.Number)
        {
            var decimals = extra.TryGetProperty("decimal_places", out var dp) ? dp.GetInt32() : 2;
            var currency = extra.TryGetProperty("currency", out var cur) ? cur.GetString() ?? "" : "";
            var used = usedCredits.GetDecimal() / (decimal)Math.Pow(10, decimals);

            decimal? total = null;
            if (root.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object)
            {
                total = SumGrantedMinorUnits(credits, "tranches") + SumGrantedMinorUnits(credits, "promo_tranches");
                if (total == 0) total = null;
                else total /= (decimal)Math.Pow(10, decimals);
            }

            creditsLine = total is null
                ? Strings.F("provider.claude.credits.used", used, currency)
                : Strings.F("provider.claude.credits.used_of", used, total, currency);
        }

        return new UsageSnapshot
        {
            ServiceName = Name,
            Ok = true,
            Bars =
            {
                new UsageBar { Label = Strings.T("provider.claude.5h"), Percent = fiveHour, ResetAt = fiveHourReset },
                new UsageBar { Label = Strings.T("provider.weekly"), Percent = sevenDayPct, ResetAt = weeklyReset },
            },
            ExtraLine = creditsLine,
        };
    }

    private static decimal SumGrantedMinorUnits(JsonElement credits, string arrayName)
    {
        if (!credits.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array) return 0;
        decimal sum = 0;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.TryGetProperty("granted_amount_minor_units", out var g) && g.ValueKind == JsonValueKind.Number)
            {
                sum += g.GetDecimal();
            }
        }
        return sum;
    }
}
