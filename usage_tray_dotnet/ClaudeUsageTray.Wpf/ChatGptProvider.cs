using System.Text.Json;

namespace ClaudeUsageTray;

public sealed class ChatGptProvider : IUsageProvider
{
    public string Name => "ChatGPT";
    public string HomeUrl => "https://chatgpt.com/#settings/Usage";
    public string LoginUrl => "https://chatgpt.com/auth/login";
    public string ProfileFolderName => "WebView2_ChatGPT";

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
            return new UsageSnapshot { ServiceName = Name, Ok = false, ErrorMessage = "Tiempo de espera agotado" };
        }

        using var doc = JsonDocument.Parse(inner);
        var root = doc.RootElement;
        if (!root.TryGetProperty("ok", out _) || root.TryGetProperty("error", out _))
        {
            return new UsageSnapshot { ServiceName = Name, Ok = false, ErrorMessage = inner };
        }

        var usage = root.GetProperty("usage");
        var rateLimit = usage.GetProperty("rate_limit");
        var bars = new List<UsageBar>();

        AddWindowBar(bars, rateLimit, "primary_window", "Límite semanal");
        AddWindowBar(bars, rateLimit, "secondary_window", "Límite corto");

        string? creditsLine = null;
        if (usage.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object
            && credits.TryGetProperty("has_credits", out var hasCredits) && hasCredits.ValueKind == JsonValueKind.True
            && credits.TryGetProperty("balance", out var balance))
        {
            creditsLine = $"Saldo de créditos: {balance.GetString()}";
        }

        return new UsageSnapshot
        {
            ServiceName = Name,
            Ok = true,
            Bars = bars,
            ExtraLine = creditsLine,
        };
    }

    private static void AddWindowBar(List<UsageBar> bars, JsonElement rateLimit, string propertyName, string label)
    {
        if (!rateLimit.TryGetProperty(propertyName, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var percent = window.TryGetProperty("used_percent", out var pct) && pct.ValueKind == JsonValueKind.Number
            ? pct.GetInt32()
            : 0;

        DateTimeOffset? resetAtValue = null;
        if (window.TryGetProperty("reset_at", out var resetAt) && resetAt.ValueKind == JsonValueKind.Number)
        {
            resetAtValue = DateTimeOffset.FromUnixTimeSeconds(resetAt.GetInt64()).ToLocalTime();
        }

        bars.Add(new UsageBar { Label = label, Percent = percent, ResetAt = resetAtValue });
    }
}
