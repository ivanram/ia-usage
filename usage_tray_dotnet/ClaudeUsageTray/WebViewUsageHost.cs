using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClaudeUsageTray;

/// <summary>
/// Hosts one WebView2 browsing context for one service (Claude or ChatGPT).
/// Used both for the one-time interactive login (window shown to the user)
/// and for silent background polling (window hidden, session reused).
/// Each instance gets its own persistent profile folder so Claude and
/// ChatGPT logins never mix.
/// </summary>
public sealed class WebViewUsageHost : Form
{
    public WebView2 WebView { get; } = new();
    public bool IsReady { get; private set; }

    private readonly string _profileFolderName;

    public WebViewUsageHost(string title, string profileFolderName)
    {
        Text = title;
        Width = 480;
        Height = 760;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ShowInTaskbar = false;
        _profileFolderName = profileFolderName;

        WebView.Dock = DockStyle.Fill;
        Controls.Add(WebView);

        FormClosing += (s, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    public async Task InitializeAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeUsageTray", _profileFolderName);
        Directory.CreateDirectory(userDataFolder);

        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
        await WebView.EnsureCoreWebView2Async(env);
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
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
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
        if (!IsReady) return null;

        await WebView.CoreWebView2.ExecuteScriptAsync(kickoffScript);

        var attempts = timeoutMs / 300;
        for (var i = 0; i < attempts; i++)
        {
            await Task.Delay(300);
            var raw = await WebView.CoreWebView2.ExecuteScriptAsync(resultExpression);
            if (raw != "null")
            {
                return System.Text.Json.JsonSerializer.Deserialize<string>(raw);
            }
        }
        return null;
    }
}
