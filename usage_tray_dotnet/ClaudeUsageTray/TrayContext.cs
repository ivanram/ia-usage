namespace ClaudeUsageTray;

public sealed class TrayContext : ApplicationContext
{
    private static readonly string StatusFile = Path.Combine(AppContext.BaseDirectory, "status.txt");

    private readonly List<IUsageProvider> _providers = new() { new ClaudeProvider(), new ChatGptProvider() };
    private readonly Dictionary<string, WebViewUsageHost> _hosts = new();
    private readonly Dictionary<string, UsageSnapshot> _lastSnapshots = new();

    private readonly NotifyIcon _trayIcon;
    private readonly TrayIconHoverDetector _hoverDetector;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly UsagePopupForm _popup = new();
    private readonly TelegramBotService _telegram;
    private AppSettings _settings = AppSettings.Load();

    public TrayContext()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Actualizar ahora", null, async (s, e) => await RefreshAllAsync());
        menu.Items.Add("Ajustes...", null, (s, e) => OpenSettings());

        var loginMenu = new ToolStripMenuItem("Iniciar sesión");
        foreach (var provider in _providers)
        {
            loginMenu.DropDownItems.Add(provider.Name, null, async (s, e) => await LoginAsync(provider));
        }
        menu.Items.Add(loginMenu);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Salir", null, (s, e) => ExitApp());

        _trayIcon = new NotifyIcon
        {
            Icon = IconFactory.BuildRobotIcon(),
            Text = "Usage Tray: iniciando...",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left) OnTrayLeftClick();
        };
        _trayIcon.DoubleClick += (s, e) => OpenSettings();

        _hoverDetector = new TrayIconHoverDetector(_trayIcon);
        _hoverDetector.HoverThresholdReached += () =>
        {
            if (_settings.PopupMode == PopupMode.Rich && !_popup.Visible) ShowPopupNow();
        };

        _popup.RefreshRequested += async (s, e) => await RefreshAllAsync();
        _popup.SettingsRequested += (s, e) => OpenSettings();

        _telegram = new TelegramBotService(() => _providers.Where(IsEnabled)
            .Select(p => _lastSnapshots.TryGetValue(p.Name, out var s) ? s : new UsageSnapshot { ServiceName = p.Name, Ok = false, ErrorMessage = "Cargando..." }));
        ApplyTelegramSettings();

        ApplyRefreshInterval();
        _timer.Tick += async (s, e) => await RefreshAllAsync();

        _ = RefreshAllAsync();
    }

    private void ApplyTelegramSettings()
    {
        if (!_settings.TelegramEnabled || string.IsNullOrWhiteSpace(_settings.TelegramBotToken))
        {
            _telegram.Stop();
            return;
        }

        _telegram.Start(_settings.TelegramBotToken, _settings.TelegramChatId, chatId =>
        {
            _settings.TelegramChatId = chatId;
            _settings.Save();
        });
    }

    private void OnTrayLeftClick()
    {
        if (_settings.PopupMode != PopupMode.Rich) return;

        if (_popup.Visible)
        {
            _popup.Hide();
            return;
        }

        ShowPopupNow();
    }

    private void ShowPopupNow()
    {
        _popup.Render(_providers.Where(IsEnabled).Select(p => _lastSnapshots.TryGetValue(p.Name, out var snap)
            ? snap
            : new UsageSnapshot { ServiceName = p.Name, Ok = false, ErrorMessage = "Cargando..." }), _settings.Theme);
        _popup.ShowNearCursor();
    }

    private bool IsEnabled(IUsageProvider p) => p switch
    {
        ClaudeProvider => _settings.ShowClaude,
        ChatGptProvider => _settings.ShowChatGpt,
        _ => false,
    };

    private void OpenSettings()
    {
        _popup.Hide();

        using var form = new SettingsForm(
            _settings,
            isLoggedIn: name => _lastSnapshots.TryGetValue(name, out var s) && s.Ok,
            triggerLogin: name =>
            {
                var provider = _providers.First(p => p.Name == name);
                _ = LoginAsync(provider);
            });
        // Windows won't reliably hand keyboard/mouse focus to a window spawned
        // from a tray-only app (it never had a foreground window to begin
        // with). Forcing TopMost briefly is the standard way to force the OS
        // to actually activate it; otherwise the dialog renders on top but
        // every click on it is silently swallowed.
        form.Shown += (s, e) =>
        {
            form.TopMost = true;
            form.Activate();
            form.TopMost = false;
        };
        if (form.ShowDialog() != DialogResult.OK) return;

        _settings = form.Result;
        _settings.Save();
        ApplyRefreshInterval();
        ApplyTelegramSettings();
        _ = RefreshAllAsync();
    }

    private void ApplyRefreshInterval()
    {
        _timer.Stop();
        _timer.Interval = Math.Max(1, _settings.RefreshMinutes) * 60 * 1000;
        _timer.Start();
    }

    private async Task<WebViewUsageHost> EnsureHostAsync(IUsageProvider provider)
    {
        if (_hosts.TryGetValue(provider.Name, out var existing)) return existing;

        var host = new WebViewUsageHost(provider.Name, provider.ProfileFolderName);
        await host.InitializeAsync();
        await host.NavigateAndWaitAsync(provider.HomeUrl);
        _hosts[provider.Name] = host;
        return host;
    }

    private static async Task<UsageSnapshot> FetchWithRetryAsync(IUsageProvider provider, WebViewUsageHost host, int attempts = 3)
    {
        UsageSnapshot last = null!;
        for (var i = 0; i < attempts; i++)
        {
            last = await provider.FetchAsync(host);
            if (last.Ok) return last;
            if (i < attempts - 1) await Task.Delay(1500);
        }
        return last;
    }

    private async Task LoginAsync(IUsageProvider provider)
    {
        var host = await EnsureHostAsync(provider);
        host.ShowLogin(provider.LoginUrl);

        while (host.Visible)
        {
            await Task.Delay(3000);
            var snap = await provider.FetchAsync(host);
            if (snap.Ok)
            {
                host.Hide();
                _lastSnapshots[provider.Name] = snap;
                UpdateTrayText();
                WriteStatusFile();
                break;
            }
        }
    }

    private async Task RefreshAllAsync()
    {
        var enabled = _providers.Where(IsEnabled).ToList();
        if (enabled.Count == 0)
        {
            _trayIcon.Text = "Usage Tray: no hay servicios activos (clic derecho → Ajustes)";
            return;
        }

        foreach (var provider in enabled)
        {
            var host = await EnsureHostAsync(provider);
            var snap = await FetchWithRetryAsync(provider, host);
            _lastSnapshots[provider.Name] = snap;

            if (!snap.Ok && !host.Visible)
            {
                // Only genuinely logged out after retries: a single transient
                // failure (slow network, a Cloudflare check settling) must
                // never force a re-login the session doesn't actually need.
                _ = LoginAsync(provider);
            }
        }

        UpdateTrayText();
        WriteStatusFile();

        if (_popup.Visible)
        {
            _popup.Render(enabled.Select(p => _lastSnapshots[p.Name]), _settings.Theme);
        }
    }

    private void UpdateTrayText()
    {
        var enabled = _providers.Where(IsEnabled).ToList();
        if (_settings.PopupMode == PopupMode.Rich)
        {
            // No native tooltip in this mode: it would flash confusingly right
            // before the 2s-hover popup takes over.
            _trayIcon.Text = "";
            return;
        }

        var lines = enabled
            .Where(p => _lastSnapshots.ContainsKey(p.Name))
            .Select(p => FullText(_lastSnapshots[p.Name]));
        _trayIcon.Text = Truncate(string.Join("\n", lines));
    }

    private static string FullText(UsageSnapshot snap)
    {
        if (!snap.Ok) return $"{snap.ServiceName}: {snap.ErrorMessage ?? "error"}";
        var bars = string.Join(", ", snap.Bars.Select(b => b.ResetAt is { } reset
            ? $"{b.Label} {b.Percent}% ({TimeFormat.RelativeShort(reset)})"
            : $"{b.Label} {b.Percent}%"));
        return $"{snap.ServiceName} - {bars}";
    }

    private static string Truncate(string s) => s.Length <= 127 ? s : s[..127];

    private void WriteStatusFile()
    {
        var text = string.Join("\n\n", _lastSnapshots.Values.Select(FullText));
        File.WriteAllText(StatusFile, $"{DateTime.Now:O}\n{text}\n");
    }

    private void ExitApp()
    {
        _trayIcon.Visible = false;
        _timer.Stop();
        _telegram.Stop();
        // Let WebView2 close its profile cleanly (flushes cookies/session data
        // to disk) instead of yanking the process out from under it.
        foreach (var host in _hosts.Values)
        {
            host.Close();
        }
        Application.Exit();
    }
}
