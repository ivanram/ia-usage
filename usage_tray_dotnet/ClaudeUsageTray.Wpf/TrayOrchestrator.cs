using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using H.NotifyIcon;
using MaterialDesignThemes.Wpf;

namespace ClaudeUsageTray;

public sealed class TrayOrchestrator : IDisposable
{
    private static readonly string StatusFile = Path.Combine(AppContext.BaseDirectory, "status.txt");
    private const int HoverThresholdMs = 2000;

    private readonly List<IUsageProvider> _providers = new() { new ClaudeProvider(), new ChatGptProvider() };
    private readonly Dictionary<string, WebViewUsageHost> _hosts = new();
    private readonly Dictionary<string, UsageSnapshot> _lastSnapshots = new();

    private TaskbarIcon _trayIcon = null!;
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private int _hoverMs;
    private bool _hoverTriggered;
    private readonly PopupWindow _popup = new();
    private readonly TelegramBotService _telegram;
    private AppSettings _settings = AppSettings.Load();

    public TrayOrchestrator()
    {
        _telegram = new TelegramBotService(() => _providers.Where(IsEnabled)
            .Select(p => _lastSnapshots.TryGetValue(p.Name, out var s) ? s : new UsageSnapshot { ServiceName = p.Name, Ok = false, ErrorMessage = "Cargando..." }));
    }

    public void Start()
    {
        ApplyTheme();

        _trayIcon = new TaskbarIcon
        {
            Icon = IconFactory.BuildRobotIcon(),
            ToolTipText = "Usage Tray: iniciando...",
            ContextMenu = BuildContextMenu(),
        };
        _trayIcon.TrayLeftMouseUp += (s, e) => OnTrayLeftClick();
        _trayIcon.TrayMouseDoubleClick += (s, e) => OpenSettings();

        _hoverTimer.Tick += (s, e) => PollHover();
        _hoverTimer.Start();

        _popup.RefreshRequested += async (s, e) => await RefreshAllAsync();
        _popup.SettingsRequested += (s, e) => { _popup.Hide(); OpenSettings(); };

        ApplyTelegramSettings();
        ApplyRefreshInterval();
        _refreshTimer.Tick += async (s, e) => await RefreshAllAsync();

        _ = RefreshAllAsync();
    }

    private void PollHover()
    {
        if (_settings.PopupMode != PopupMode.Rich || _popup.IsVisible)
        {
            _hoverMs = 0;
            _hoverTriggered = false;
            return;
        }

        // TaskbarIcon is a real FrameworkElement under the hood, and
        // H.NotifyIcon keeps IsMouseOver in sync with the actual tray icon
        // hover state — no need for the Shell_NotifyIconGetRect P/Invoke
        // hack the WinForms version needed.
        if (_trayIcon.IsMouseOver)
        {
            _hoverMs += (int)_hoverTimer.Interval.TotalMilliseconds;
            if (_hoverMs >= HoverThresholdMs && !_hoverTriggered)
            {
                _hoverTriggered = true;
                ShowPopupNow();
            }
        }
        else
        {
            _hoverMs = 0;
            _hoverTriggered = false;
        }
    }

    private void OnTrayLeftClick()
    {
        if (_settings.PopupMode != PopupMode.Rich) return;

        if (_popup.IsVisible)
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
            : new UsageSnapshot { ServiceName = p.Name, Ok = false, ErrorMessage = "Cargando..." }));
        _popup.ShowNearCursor();
    }

    private bool IsEnabled(IUsageProvider p) => p switch
    {
        ClaudeProvider => _settings.ShowClaude,
        ChatGptProvider => _settings.ShowChatGpt,
        _ => false,
    };

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var refreshItem = new MenuItem { Header = "Actualizar ahora" };
        refreshItem.Click += async (s, e) => await RefreshAllAsync();
        menu.Items.Add(refreshItem);

        var settingsItem = new MenuItem { Header = "Ajustes..." };
        settingsItem.Click += (s, e) => OpenSettings();
        menu.Items.Add(settingsItem);

        var loginMenu = new MenuItem { Header = "Iniciar sesión" };
        foreach (var provider in _providers)
        {
            var item = new MenuItem { Header = provider.Name };
            item.Click += async (s, e) => await LoginAsync(provider);
            loginMenu.Items.Add(item);
        }
        menu.Items.Add(loginMenu);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "Salir" };
        exitItem.Click += (s, e) => ExitApp();
        menu.Items.Add(exitItem);

        return menu;
    }

    private void OpenSettings()
    {
        _popup.Hide();

        var window = new SettingsWindow(
            _settings,
            isLoggedIn: name => _lastSnapshots.TryGetValue(name, out var s) && s.Ok,
            triggerLogin: name =>
            {
                var provider = _providers.First(p => p.Name == name);
                _ = LoginAsync(provider);
            });

        window.ShowDialog();
        if (!window.Saved) return;

        _settings = window.Result;
        _settings.Save();
        ApplyTheme();
        ApplyRefreshInterval();
        ApplyTelegramSettings();
        _ = RefreshAllAsync();
    }

    private void ApplyTheme()
    {
        var isDark = ThemeHelper.ResolveIsDark(_settings.Theme);
        var paletteHelper = new PaletteHelper();
        var theme = paletteHelper.GetTheme();
        theme.SetBaseTheme(isDark ? BaseTheme.Dark : BaseTheme.Light);
        paletteHelper.SetTheme(theme);
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

    private void ApplyRefreshInterval()
    {
        _refreshTimer.Stop();
        _refreshTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.RefreshMinutes));
        _refreshTimer.Start();
    }

    private static readonly string DebugFile = Path.Combine(AppContext.BaseDirectory, "orchestrator_debug.txt");
    private static void Log(string msg) => File.AppendAllText(DebugFile, $"{DateTime.Now:O} {msg}\n");

    private async Task<WebViewUsageHost> EnsureHostAsync(IUsageProvider provider)
    {
        if (_hosts.TryGetValue(provider.Name, out var existing)) return existing;

        Log($"[{provider.Name}] creating host...");
        var host = new WebViewUsageHost(provider.Name, provider.ProfileFolderName);
        Log($"[{provider.Name}] calling InitializeAsync...");
        await host.InitializeAsync();
        Log($"[{provider.Name}] InitializeAsync done, IsReady={host.IsReady}. Navigating to {provider.HomeUrl}...");
        await host.NavigateAndWaitAsync(provider.HomeUrl);
        Log($"[{provider.Name}] NavigateAndWaitAsync done.");
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
        Log($"[{provider.Name}] LoginAsync triggered");
        var host = await EnsureHostAsync(provider);
        host.ShowLogin(provider.LoginUrl);

        while (host.IsVisible)
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
        Log("RefreshAllAsync start");
        var enabled = _providers.Where(IsEnabled).ToList();
        Log($"enabled providers: {string.Join(",", enabled.Select(p => p.Name))}");
        if (enabled.Count == 0)
        {
            _trayIcon.ToolTipText = "Usage Tray: no hay servicios activos (clic derecho → Ajustes)";
            return;
        }

        foreach (var provider in enabled)
        {
            var host = await EnsureHostAsync(provider);
            Log($"[{provider.Name}] fetching with retry...");
            var snap = await FetchWithRetryAsync(provider, host);
            Log($"[{provider.Name}] fetch result Ok={snap.Ok} Error={snap.ErrorMessage}");
            _lastSnapshots[provider.Name] = snap;

            if (!snap.Ok && !host.IsVisible)
            {
                _ = LoginAsync(provider);
            }
        }

        UpdateTrayText();
        WriteStatusFile();

        if (_popup.IsVisible)
        {
            _popup.Render(enabled.Select(p => _lastSnapshots[p.Name]));
        }
    }

    private void UpdateTrayText()
    {
        var enabled = _providers.Where(IsEnabled).ToList();
        if (_settings.PopupMode == PopupMode.Rich)
        {
            _trayIcon.ToolTipText = "Usage Tray";
            return;
        }

        var lines = enabled
            .Where(p => _lastSnapshots.ContainsKey(p.Name))
            .Select(p => FullText(_lastSnapshots[p.Name]));
        _trayIcon.ToolTipText = Truncate(string.Join("\n", lines));
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
        Dispose();
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _trayIcon.Dispose();
        _refreshTimer.Stop();
        _hoverTimer.Stop();
        _telegram.Stop();
        foreach (var host in _hosts.Values)
        {
            host.CloseForReal();
        }
    }
}
