using System.Text.Json;

namespace ClaudeUsageTray;

/// <summary>
/// Grok has no documented public usage endpoint, but grok.com's own web app
/// calls an internal one — confirmed by inspecting the source of a public
/// open-source usage-tracking browser extension (JoshuaWang2211/grok-usage-watch),
/// which hits this same URL with the browser's normal logged-in cookies.
/// </summary>
public sealed class GrokProvider : IUsageProvider
{
    public string Name => "Grok";
    public string HomeUrl => "https://grok.com";
    public string LoginUrl => "https://grok.com";
    public string ProfileFolderName => "WebView2_Grok";

    private const string KickoffScript = """
        window.__grokUsageResult = null;
        (async () => {
          try {
            const resp = await fetch('https://grok.com/rest/rate-limits', {
              method: 'POST',
              credentials: 'include',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ requestKind: 'DEFAULT', modelName: 'grok-4' }),
            });
            if (!resp.ok) { window.__grokUsageResult = JSON.stringify({error: 'http_' + resp.status}); return; }
            const data = await resp.json();
            window.__grokUsageResult = JSON.stringify({ok:true, data});
          } catch (e) {
            window.__grokUsageResult = JSON.stringify({error:String(e)});
          }
        })();
        """;

    private const string PollExpression = "window.__grokUsageResult";

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

        // grok.com's rate-limits endpoint answers 200 with a small
        // anonymous/guest allowance even when logged out — it never 401s
        // the way you'd expect, so a plain "did the fetch succeed" check
        // can't tell a guest quota from a real account. A genuine auth
        // cookie can't be faked by an anonymous session, so require one
        // before trusting this as real usage data (otherwise the login
        // window was closing itself the instant it loaded, before the
        // user had a chance to type anything).
        if (!await host.HasAuthCookieAsync(HomeUrl))
        {
            return new UsageSnapshot { ServiceName = Name, Ok = false, ErrorMessage = Strings.T("provider.grok.loginneeded") };
        }

        var data = root.GetProperty("data");
        var total = data.TryGetProperty("totalQueries", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
        var remaining = data.TryGetProperty("remainingQueries", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : total;
        var percent = total > 0 ? (int)Math.Round((total - remaining) / (double)total * 100) : 0;

        // The endpoint gives a rolling window length, not an absolute reset
        // timestamp, so this is an approximation: "now + window length"
        // rather than a precisely tracked start-of-window time.
        DateTimeOffset? resetAt = null;
        if (remaining < total && data.TryGetProperty("windowSizeSeconds", out var w) && w.ValueKind == JsonValueKind.Number)
        {
            resetAt = DateTimeOffset.Now.AddSeconds(w.GetInt32());
        }

        return new UsageSnapshot
        {
            ServiceName = Name,
            Ok = true,
            Bars = new List<UsageBar> { new() { Label = Strings.T("provider.grok.usage"), Percent = percent, ResetAt = resetAt, IsPrimary = true, Qualifier = Strings.T("qualifier.usage"), ShortPrefix = Strings.T("prefix.usage") } },
        };
    }
}
