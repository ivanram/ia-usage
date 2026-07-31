using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ClaudeUsageTray;

/// <summary>
/// Hosts one WebView2 browsing context for one service (Claude or ChatGPT).
/// Used both for the one-time interactive login (window shown to the user)
/// and for silent background polling (window hidden, session reused).
/// Each instance gets its own persistent profile folder so Claude and
/// ChatGPT logins never mix.
/// </summary>
public sealed class WebViewUsageHost : Window
{
    public WebView2 WebView { get; } = new();
    public bool IsReady { get; private set; }

    private readonly string _profileFolderName;
    private bool _reallyClose;

    public WebViewUsageHost(string title, string profileFolderName)
    {
        Title = title;
        Width = 480;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // Start parked off-screen rather than never-shown: WPF only runs a
        // real layout/Loaded pass once a window is actually shown, and
        // WebView2's WPF control needs that pass to parent its child HWND —
        // just forcing the top-level HWND via WindowInteropHelper wasn't
        // enough, EnsureCoreWebView2Async hung forever without this.
        Left = -32000;
        Top = -32000;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        _profileFolderName = profileFolderName;

        Content = WebView;

        // Closing the window (X) just hides it, keeps the session alive.
        // ExitApp() sets _reallyClose first so app shutdown can actually close it.
        Closing += (s, e) =>
        {
            if (!_reallyClose)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    private static readonly string DebugFile = Path.Combine(Paths.LogsDir, "webview_debug.txt");
    private static void Log(string msg) => File.AppendAllText(DebugFile, $"{DateTime.Now:O} {msg}\n");

    public async Task InitializeAsync()
    {
        Log($"[{Title}] InitializeAsync: showing off-screen to force layout...");
        Show();
        // Windows clamps a window positioned entirely outside every
        // monitor back onto the visible desktop (usually leaving just a
        // sliver of its title bar poking up above the taskbar) — that's
        // what the -32000/-32000 parking spot alone produced. Show() has
        // already done its job (forced the real layout/Loaded pass
        // WebView2 needs), so immediately hide the window for real instead
        // of leaving it "shown" somewhere Windows decided was valid.
        Hide();
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Log($"[{Title}] HWND={handle}");

        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeUsageTray", _profileFolderName);
        Directory.CreateDirectory(userDataFolder);
        Log($"[{Title}] calling CoreWebView2Environment.CreateAsync, folder={userDataFolder}");

        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
        Log($"[{Title}] environment created, calling EnsureCoreWebView2Async...");
        await WebView.EnsureCoreWebView2Async(env);
        Log($"[{Title}] EnsureCoreWebView2Async done.");
        IsReady = true;
    }

    public Task NavigateAndWaitAsync(string url)
    {
        var tcs = new TaskCompletionSource();
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            WebView.CoreWebView2.NavigationCompleted -= Handler;
            tcs.TrySetResult();
        }
        WebView.CoreWebView2.NavigationCompleted += Handler;
        WebView.CoreWebView2.Navigate(url);
        return tcs.Task;
    }

    public void ShowLogin(string loginUrl)
    {
        WebView.CoreWebView2.Navigate(loginUrl);
        // Bring back from the off-screen parking spot onto the primary screen.
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// Some providers' "am I logged in" signal isn't reliable on its own —
    /// grok.com's rate-limits endpoint, for instance, returns 200 with a
    /// small anonymous/guest allowance instead of 401 when logged out,
    /// which looked exactly like a successful fetch and closed the login
    /// window before the user had typed anything. A real auth/session
    /// cookie is a much harder thing to fake, so providers that hit this
    /// can require one before trusting an otherwise-successful fetch.
    /// </summary>
    public async Task<bool> HasAuthCookieAsync(string url)
    {
        var cookies = await WebView.CoreWebView2.CookieManager.GetCookiesAsync(url);
        return cookies.Any(c =>
            c.Name.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
            c.Name.Contains("session", StringComparison.OrdinalIgnoreCase) ||
            c.Name.Contains("sso", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Runs <paramref name="kickoffScript"/> (which must eventually assign its
    /// JSON-string result to <paramref name="resultExpression"/>, e.g. a
    /// window property) and polls that expression until it stops being
    /// "null". Works around ExecuteScriptAsync not reliably awaiting
    /// returned promises in this WebView2 runtime.
    /// </summary>
    public async Task<string?> RunScriptForResultAsync(string kickoffScript, string resultExpression, int timeoutMs = 9000)
    {
        var debugFile = Path.Combine(Paths.LogsDir, "webview_debug.txt");
        if (!IsReady)
        {
            File.AppendAllText(debugFile, $"{DateTime.Now:O} [{Title}] IsReady=false, bailing out\n");
            return null;
        }

        var kickoffResult = await WebView.CoreWebView2.ExecuteScriptAsync(kickoffScript);
        File.AppendAllText(debugFile, $"{DateTime.Now:O} [{Title}] kickoff executed, raw return={kickoffResult}\n");

        var attempts = timeoutMs / 300;
        for (var i = 0; i < attempts; i++)
        {
            await Task.Delay(300);
            var raw = await WebView.CoreWebView2.ExecuteScriptAsync(resultExpression);
            if (i % 5 == 0) File.AppendAllText(debugFile, $"{DateTime.Now:O} [{Title}] attempt {i}: raw={raw}\n");
            if (raw != "null")
            {
                return System.Text.Json.JsonSerializer.Deserialize<string>(raw);
            }
        }
        return null;
    }

    /// <summary>Lets the app's shutdown path actually close this window instead of hiding it.</summary>
    public void CloseForReal()
    {
        _reallyClose = true;
        Close();
    }
}
