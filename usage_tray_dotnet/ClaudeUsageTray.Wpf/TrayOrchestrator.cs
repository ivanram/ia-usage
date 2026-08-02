using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using H.NotifyIcon;

namespace ClaudeUsageTray;

public sealed class TrayOrchestrator : IDisposable
{
    private static readonly string StatusFile = Path.Combine(Paths.LogsDir, "status.txt");
    private static readonly Guid TrayIconGuid = new("699af4c8-abef-4fbe-bade-983a54433124");

    private readonly List<IUsageProvider> _providers = new() { new ClaudeProvider(), new ChatGptProvider(), new GrokProvider() };
    private readonly Dictionary<string, WebViewUsageHost> _hosts = new();
    // Caches the in-flight creation Task, not just the finished host — a
    // concurrent RefreshAllAsync tick and a manual "Iniciar sesión" click
    // (or two overlapping refresh ticks) could both see an empty _hosts
    // dictionary and each start their own WebViewUsageHost for the same
    // provider before either finished, since the old check-then-create
    // wasn't atomic. That produced two live host windows for one provider
    // (confirmed in webview_debug.txt: two "InitializeAsync" calls for
    // Grok seconds apart) — whichever one lost the race got orphaned but
    // could still be visible, making the login window look like it
    // randomly closed when the other instance took over.
    private readonly Dictionary<string, Task<WebViewUsageHost>> _hostTasks = new();
    private readonly Dictionary<string, UsageSnapshot> _lastSnapshots = new();
    private readonly Dictionary<string, int> _lastPercents = new();
    private static SoundPlayer? _chimePlayer;
    private static SoundPlayer? _tromboneSoundPlayer;
    private DateTime? _lastUpdated;

    private TaskbarIcon _trayIcon = null!;
    private readonly ContextMenu _contextMenu = new();
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private int _hoverMs;
    private bool _hoverTriggered;
    private readonly PopupWindow _popup = new();
    private readonly TelegramBotService _telegram;
    private readonly UsageHistoryStore _historyStore = new();
    private StatsWindow? _statsWindow;
    private AppSettings _settings = AppSettings.Load();

    public TrayOrchestrator()
    {
        _telegram = new TelegramBotService(
            () => _providers.Where(IsEnabled)
                .Select(p => _lastSnapshots.TryGetValue(p.Name, out var s) ? s : new UsageSnapshot { ServiceName = p.Name, Ok = false, ErrorMessage = Strings.T("loading") }),
            _historyStore);
    }

    public void Start()
    {
        Strings.Current = _settings.Language;
        ApplyTheme();

        PopulateContextMenu();

        _trayIcon = new TaskbarIcon
        {
            Id = TrayIconGuid,
            Icon = IconFactory.BuildRobotIcon(),
            ToolTipText = _settings.PopupMode == PopupMode.Rich ? Strings.T("app.name") : Strings.F("tray.tooltip.starting", Strings.T("app.name")),
            ContextMenu = _contextMenu,
        };
        _trayIcon.TrayLeftMouseUp += (s, e) => OnTrayLeftClick();
        _trayIcon.TrayMouseDoubleClick += (s, e) => OpenSettings();

        // TaskbarIcon normally creates its native icon from its own Loaded
        // event, which only fires once it's parented into a visual tree.
        // We construct it standalone in code, so it never loads on its own —
        // force creation explicitly instead.
        _trayIcon.ForceCreate(enablesEfficiencyMode: false);
        var registered = NativeScreenHelper.TryGetTrayIconRect(TrayIconGuid, out var iconRect);
        Log($"[tray] ForceCreate done. Shell_NotifyIconGetRect success={registered} rect=({iconRect.Left},{iconRect.Top},{iconRect.Right},{iconRect.Bottom})");

        _hoverTimer.Tick += (s, e) => PollHover();
        _hoverTimer.Start();

        _popup.RefreshRequested += async (s, e) => await RefreshAllAsync();
        _popup.SettingsRequested += (s, e) => { _popup.Hide(); OpenSettings(); };
        _popup.StatsRequested += (s, e) => OpenStats();
        _popup.WarmUp();

        ApplyTelegramSettings();
        ApplyRefreshInterval();
        _refreshTimer.Tick += async (s, e) => await RefreshAllAsync();

        _ = RefreshAllAsync();
    }

    private int _awayMs;
    private const int AwayGraceMs = 700;

    private void PollHover()
    {
        if (_settings.PopupMode != PopupMode.Rich)
        {
            _hoverMs = 0;
            _hoverTriggered = false;
            return;
        }

        // TaskbarIcon.IsMouseOver doesn't track real hover state: the tray
        // icon is a native shell icon, not an actually-rendered WPF visual,
        // so WPF's own mouse-over bookkeeping never applies to it. Instead
        // we ask the shell directly for the icon's on-screen rect (by the
        // fixed GUID assigned in Start()) and poll the real cursor position
        // against it — the same technique the WinForms build used.
        var cursor = NativeScreenHelper.GetCursorPosition();
        var isOverIcon = NativeScreenHelper.TryGetTrayIconRect(TrayIconGuid, out var iconRect)
            && NativeScreenHelper.Contains(iconRect, cursor);

        if (_popup.IsVisible)
        {
            // Pinned panels never auto-hide, regardless of cursor position —
            // that's the whole point of pinning one.
            if (_popup.IsPinned)
            {
                _awayMs = 0;
                return;
            }

            // Auto-dismiss once the cursor has been away from both the icon
            // and the panel itself for a short grace period — long enough to
            // move the cursor from the icon up into the panel without it
            // flickering shut mid-transition.
            if (isOverIcon || IsCursorOverPopup(cursor))
            {
                _awayMs = 0;
            }
            else
            {
                _awayMs += (int)_hoverTimer.Interval.TotalMilliseconds;
                if (_awayMs >= AwayGraceMs)
                {
                    Log($"[away-hide] cursor=({cursor.X},{cursor.Y}) isOverIcon={isOverIcon} popup=({_popup.Left},{_popup.Top},{_popup.ActualWidth}x{_popup.ActualHeight}) IsVisible={_popup.IsVisible}");
                    _popup.Hide();
                    _awayMs = 0;
                    _hoverMs = 0;
                    _hoverTriggered = false;
                }
            }
            return;
        }

        if (isOverIcon)
        {
            _hoverMs += (int)_hoverTimer.Interval.TotalMilliseconds;
            var thresholdMs = Math.Max(0, _settings.HoverDelaySeconds) * 1000;
            if (_hoverMs >= thresholdMs && !_hoverTriggered)
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

    private bool IsCursorOverPopup(NativeScreenHelper.POINT cursor)
    {
        // A suspiciously tiny size means a layout pass hasn't caught up
        // with a just-applied resize (or produced a bad measurement) —
        // treat that as "can't tell" and assume the cursor IS still over
        // it rather than risk closing a panel the user is actively
        // looking at because of a transient bad reading.
        if (_popup.ActualWidth < 50 || _popup.ActualHeight < 50) return true;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_popup);
        var rect = new NativeScreenHelper.RECT
        {
            Left = (int)(_popup.Left * dpi.DpiScaleX),
            Top = (int)(_popup.Top * dpi.DpiScaleY),
            Right = (int)((_popup.Left + _popup.ActualWidth) * dpi.DpiScaleX),
            Bottom = (int)((_popup.Top + _popup.ActualHeight) * dpi.DpiScaleY),
        };
        return NativeScreenHelper.Contains(rect, cursor);
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
        var enabled = _providers.Where(IsEnabled).ToList();
        // Only ever render services that have real data (success or a real
        // fetch failure) — never a synthetic "loading" placeholder. If a
        // service isn't ready yet it's simply left out; RefreshAllAsync
        // re-renders the open popup the moment its data arrives.
        var ready = enabled.Where(p => _lastSnapshots.ContainsKey(p.Name)).Select(p => _lastSnapshots[p.Name]);
        _popup.Render(ready, hasAnyEnabled: enabled.Count > 0, lastUpdated: _lastUpdated, totalEnabled: enabled.Count);
        _popup.ShowNearCursor();
    }

    private bool IsEnabled(IUsageProvider p) => p switch
    {
        ClaudeProvider => _settings.ShowClaude,
        ChatGptProvider => _settings.ShowChatGpt,
        GrokProvider => _settings.ShowGrok,
        _ => false,
    };

    // Segoe MDL2 Assets glyphs, same font/codepoints the rest of the app
    // already uses for its own icon buttons (PopupWindow's refresh/settings
    // icons, SettingsWindow's card headers) — keeps the right-click menu
    // from looking like a bare, un-styled default Windows menu.
    private const string MenuIconRefresh = "";
    private const string MenuIconSettings = "";
    private const string MenuIconLogin = "";
    private const string MenuIconExit = "";

    private static TextBlock MenuGlyph(string glyph) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = 14,
        Foreground = (Brush)new BrushConverter().ConvertFrom("#888888")!,
    };

    /// <summary>
    /// A plain default ContextMenu/MenuItem never picks up the app's own
    /// dark/light theme (it's always the stock Windows menu look), which
    /// reads as visually disconnected from the rest of the UI — this pins
    /// its colors to the same MaterialDesign resources everything else
    /// uses, so it actually follows Ajustes → Apariencia. Padding/height are
    /// set directly per item rather than through ItemContainerStyle — a
    /// shared Style object assigned there was the suspected cause of the
    /// tray icon's right-click crash (H.NotifyIcon.Wpf hosts this menu
    /// through its own native popup plumbing, which apparently doesn't get
    /// along with a reused ItemContainerStyle); setting properties directly
    /// on each MenuItem gets the same look without touching it.
    /// </summary>
    private static void StyleMenuItem(MenuItem item)
    {
        item.SetResourceReference(Control.ForegroundProperty, "MaterialDesignBody");
        item.Padding = new Thickness(10, 7, 14, 7);
        item.MinHeight = 32;
    }

    /// <summary>
    /// Fills _contextMenu in place — never replaces the ContextMenu object
    /// itself. H.NotifyIcon.Wpf's TaskbarIcon.ForceCreate binds native
    /// tray-icon plumbing to the exact ContextMenu instance handed to it at
    /// construction; swapping in a brand new one afterward (which the
    /// language-preview code used to do on every Settings save/cancel) left
    /// that plumbing pointing at a stale object and crashed the whole app
    /// the next time the icon was right-clicked. Called once at startup and
    /// again — via PreviewLanguage, clearing Items first — whenever the
    /// language changes live.
    /// </summary>
    private void PopulateContextMenu()
    {
        _contextMenu.Items.Clear();

        var refreshItem = new MenuItem { Header = Strings.T("menu.refresh"), Icon = MenuGlyph(MenuIconRefresh) };
        refreshItem.Click += async (s, e) => await RefreshAllAsync();
        StyleMenuItem(refreshItem);
        _contextMenu.Items.Add(refreshItem);

        var settingsItem = new MenuItem { Header = Strings.T("menu.settings"), Icon = MenuGlyph(MenuIconSettings) };
        settingsItem.Click += (s, e) => OpenSettings();
        StyleMenuItem(settingsItem);
        _contextMenu.Items.Add(settingsItem);

        var loginMenu = new MenuItem { Header = Strings.T("menu.login"), Icon = MenuGlyph(MenuIconLogin) };
        StyleMenuItem(loginMenu);
        foreach (var provider in _providers.Where(p => p.SupportsLogin))
        {
            var item = new MenuItem { Header = provider.Name };
            item.Click += async (s, e) => await LoginAsync(provider);
            StyleMenuItem(item);
            loginMenu.Items.Add(item);
        }
        _contextMenu.Items.Add(loginMenu);

        _contextMenu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = Strings.T("menu.exit"), Icon = MenuGlyph(MenuIconExit) };
        exitItem.Click += (s, e) => ExitApp();
        StyleMenuItem(exitItem);
        _contextMenu.Items.Add(exitItem);

        _contextMenu.SetResourceReference(Control.BackgroundProperty, "MaterialDesignPaper");
        _contextMenu.SetResourceReference(Control.ForegroundProperty, "MaterialDesignBody");
        _contextMenu.BorderThickness = new Thickness(1);
        _contextMenu.SetResourceReference(Control.BorderBrushProperty, "MaterialDesignDivider");
    }

    private SettingsWindow? _openSettingsWindow;

    private void OpenSettings()
    {
        _popup.Hide();

        if (_openSettingsWindow is not null)
        {
            _openSettingsWindow.Activate();
            return;
        }

        var window = new SettingsWindow(
            _settings,
            isLoggedIn: name => _lastSnapshots.TryGetValue(name, out var s) && s.Ok,
            triggerLogin: name =>
            {
                var provider = _providers.First(p => p.Name == name);
                _ = LoginAsync(provider);
            },
            previewTheme: ThemeHelper.Apply,
            previewLanguage: PreviewLanguage);
        _openSettingsWindow = window;

        window.ShowDialog();
        _openSettingsWindow = null;

        if (!window.Saved)
        {
            ApplyTheme(); // revert any live theme preview back to the saved setting
            PreviewLanguage(_settings.Language); // same revert, for the language preview
            return;
        }

        _settings = window.Result;
        _settings.Save();
        ApplyTheme();
        PreviewLanguage(_settings.Language);
        ApplyRefreshInterval();
        ApplyTelegramSettings();
        _ = RefreshAllAsync();
    }

    /// <summary>Applies a language process-wide and rebuilds anything with text already baked in at construction time — currently just the tray context menu.</summary>
    private void PreviewLanguage(AppLanguage language)
    {
        Strings.Current = language;
        PopulateContextMenu();
    }

    /// <summary>
    /// Unlike Settings, this isn't modal and stays open on its own — a
    /// second click just brings the existing window forward instead of
    /// opening a duplicate, same pattern as _openSettingsWindow. Pins the
    /// main panel instead of hiding it, since the whole point of Stats
    /// opening beside it is for both to stay visible together — the user
    /// can still unpin (or close either window) by hand.
    /// </summary>
    private void OpenStats()
    {
        var anchor = new Rect(_popup.Left, _popup.Top, _popup.ActualWidth, _popup.ActualHeight);
        _popup.SetPinned(true);

        if (_statsWindow is not null)
        {
            _statsWindow.Activate();
            return;
        }

        _statsWindow = new StatsWindow(_providers.Where(IsEnabled).Select(p => p.Name).ToList(), _historyStore, anchor);
        _statsWindow.Closed += (s, e) => _statsWindow = null;
        _statsWindow.Show();
    }

    private void ApplyTheme()
    {
        ThemeHelper.Apply(_settings.Theme);
        ThemeHelper.ApplyAccent(_settings.AccentColor);
        _popup.FlatBarColorHex = _settings.AccentColor == AppSettings.OriginalAccentSentinel ? null : _settings.AccentColor;
        _popup.AnimationsEnabled = _settings.AnimationsEnabled;
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

    private static readonly string DebugFile = Path.Combine(Paths.LogsDir, "orchestrator_debug.txt");
    private static readonly object LogLock = new();

    // Providers are now fetched in parallel (see RefreshAllAsync), which
    // means their Log() calls can genuinely land at the same instant —
    // File.AppendAllText opening the same file from two places at once
    // throws IOException, which (uncaught, inside RefreshAllAsync's loop)
    // silently killed the rest of that refresh cycle, including whatever
    // CheckForReset call would've fired the exhausted-limit toast for a
    // provider later in the batch. Locked and best-effort now, same as
    // every other debug logger in this app.
    private static void Log(string msg)
    {
        lock (LogLock)
        {
            try { File.AppendAllText(DebugFile, $"{DateTime.Now:O} {msg}\n"); }
            catch (Exception ex)
            {
                try { File.AppendAllText(Path.Combine(Paths.LogsDir, "log_failures.txt"), $"{DateTime.Now:O} orchestrator Log failed: {ex}\n"); }
                catch { /* truly nothing more we can do */ }
            }
        }
    }

    private Task<WebViewUsageHost> EnsureHostAsync(IUsageProvider provider)
    {
        // Reuse the in-flight Task itself (not just the finished result) so
        // two overlapping callers — a refresh tick and a manual login click,
        // say — await the SAME host creation instead of each racing to
        // create their own. See the _hostTasks field comment for the bug
        // this caused.
        if (_hostTasks.TryGetValue(provider.Name, out var existing)) return existing;

        var task = CreateHostAsync(provider);
        _hostTasks[provider.Name] = task;
        return task;
    }

    private async Task<WebViewUsageHost> CreateHostAsync(IUsageProvider provider)
    {
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
            _trayIcon.ToolTipText = Strings.F("tray.tooltip.noservices", Strings.T("app.name"));
            return;
        }

        if (_popup.IsVisible) _popup.SetRefreshing(true);
        try
        {
            // Fetched in parallel rather than one provider at a time — each
            // host creation + page load is I/O-bound (WebView2 navigating a
            // real site out-of-process), so kicking all of them off at once
            // lets their waits overlap in wall-clock time instead of adding
            // up. This is what actually matters for the very first refresh
            // right after launch: with three services fetched sequentially,
            // the last one wouldn't have data for as long as it took all
            // three combined.
            var pending = enabled.ToDictionary(p => p, FetchOneAsync);
            while (pending.Count > 0)
            {
                var finishedTask = await Task.WhenAny(pending.Values);
                var provider = pending.First(kv => kv.Value == finishedTask).Key;
                pending.Remove(provider);
                var (host, snap) = await finishedTask;

                Log($"[{provider.Name}] fetch result Ok={snap.Ok} Error={snap.ErrorMessage}");
                if (snap.Ok)
                {
                    CheckForReset(provider.Name, snap);
                    var primaryBar = snap.Bars.FirstOrDefault(b => b.IsPrimary);
                    if (primaryBar is not null) _historyStore.Record(provider.Name, primaryBar.Percent);
                }
                _lastSnapshots[provider.Name] = snap;

                if (!snap.Ok && !host.IsVisible && provider.SupportsLogin)
                {
                    _ = LoginAsync(provider);
                }

                // Re-render an already-open popup as each service's real data
                // lands, instead of waiting for the whole batch — this is what
                // replaces the old "Cargando..." placeholder for a service that
                // wasn't ready yet when the popup was first opened. PopupWindow
                // itself skips re-animating any bar whose value hasn't changed
                // since the last render, so a provider that already had data
                // doesn't replay its animation just because a later provider
                // arrived — only render exactly once per provider here, no
                // separate render after the loop repeating the same data.
                _lastUpdated = DateTime.Now;
                if (_popup.IsVisible)
                {
                    var ready = enabled.Where(p => _lastSnapshots.ContainsKey(p.Name)).Select(p => _lastSnapshots[p.Name]);
                    _popup.Render(ready, hasAnyEnabled: true, lastUpdated: _lastUpdated, totalEnabled: enabled.Count);
                }
            }

            UpdateTrayText();
            WriteStatusFile();
        }
        finally
        {
            if (_popup.IsVisible) _popup.SetRefreshing(false);
        }
    }

    private async Task<(WebViewUsageHost Host, UsageSnapshot Snap)> FetchOneAsync(IUsageProvider provider)
    {
        var host = await EnsureHostAsync(provider);
        Log($"[{provider.Name}] fetching with retry...");
        var snap = await FetchWithRetryAsync(provider, host);
        return (host, snap);
    }

    /// <summary>
    /// A reset shows up as a bar's percent dropping compared to the last
    /// reading — usage only ever climbs within a window otherwise. The
    /// small margin (4 points) and floor (previous reading >= 10%) keep
    /// ordinary jitter from a re-fetch from being mistaken for a reset.
    /// The reset check needs a real previous reading to compare against,
    /// so it never fires on a bar's very first reading — but the exhausted
    /// check is the opposite: _lastPercents is only ever in-memory, so
    /// "first reading" here really means "since this app instance
    /// started", and if the bar is already sitting at 100% right then, the
    /// user should still hear about it once per launch rather than only
    /// catching a future 0->100 crossing they might never see.
    /// </summary>
    private void CheckForReset(string serviceName, UsageSnapshot snap)
    {
        var notifyEnabled = IsNotifyEnabled(serviceName);
        NotifyLog($"CheckForReset [{serviceName}] notifyEnabled={notifyEnabled} bars={string.Join(",", snap.Bars.Select(b => $"{b.Label}={b.Percent}%"))}");
        if (!notifyEnabled) return;

        var resetDetected = false;
        var exhaustedDetected = false;
        var eightyDetected = false;
        foreach (var bar in snap.Bars)
        {
            var key = $"{serviceName}|{bar.Label}";
            if (_lastPercents.TryGetValue(key, out var prev))
            {
                if (prev >= 10 && bar.Percent < prev - 4) resetDetected = true;
                // Fires once on the crossing into 100%/80%, not on every
                // refresh that happens to still read above the line afterward.
                if (prev < 100 && bar.Percent >= 100) exhaustedDetected = true;
                if (prev < 80 && bar.Percent >= 80) eightyDetected = true;
                NotifyLog($"  [{key}] prev={prev} now={bar.Percent} resetDetected={resetDetected} exhaustedDetected={exhaustedDetected}");
            }
            else
            {
                if (bar.Percent >= 100) exhaustedDetected = true;
                else if (bar.Percent >= 80) eightyDetected = true;
                NotifyLog($"  [{key}] no previous reading, now={bar.Percent} exhaustedDetected={exhaustedDetected}");
            }
            _lastPercents[key] = bar.Percent;
        }

        NotifyLog($"CheckForReset [{serviceName}] result: resetDetected={resetDetected} exhaustedDetected={exhaustedDetected}");
        if (resetDetected) ShowResetToast(serviceName);
        if (exhaustedDetected) ShowExhaustedToast(serviceName);
        // 80% is a Telegram-only heads-up — no desktop toast/sound for it,
        // matching the user's ask for a lighter-weight "just so you know"
        // separate from the more attention-grabbing reset/exhausted ones.
        if (eightyDetected && _settings.TelegramNotifyUsage && _settings.TelegramNotify80Percent)
        {
            _ = _telegram.SendNotificationAsync(Strings.F("telegram.notify80.message", serviceName));
        }
    }

    private static readonly object NotifyLogLock = new();
    private static void NotifyLog(string msg)
    {
        lock (NotifyLogLock)
        {
            try { File.AppendAllText(Path.Combine(Paths.LogsDir, "notify_debug.txt"), $"{DateTime.Now:O} {msg}\n"); }
            catch { /* best effort */ }
        }
    }

    private bool IsNotifyEnabled(string serviceName) => serviceName switch
    {
        "Claude" => _settings.NotifyResetClaude,
        "ChatGPT" => _settings.NotifyResetChatGpt,
        "Grok" => _settings.NotifyResetGrok,
        _ => false,
    };

    private void ShowResetToast(string serviceName)
    {
        var message = Strings.F("toast.reset", serviceName);
        var toast = new ToastWindow();
        toast.ShowNear(serviceName, message);
        if (_settings.NotifySoundEnabled) PlaySound(ref _chimePlayer, "chime.wav");
        if (_settings.TelegramNotifyUsage) _ = _telegram.SendNotificationAsync(message);
    }

    private void ShowExhaustedToast(string serviceName)
    {
        NotifyLog($"ShowExhaustedToast [{serviceName}] entered");
        var message = Strings.F("toast.exhausted", serviceName);
        try
        {
            var toast = new ToastWindow();
            NotifyLog($"ShowExhaustedToast [{serviceName}] message='{message}', calling ShowNear...");
            toast.ShowNear(serviceName, message);
            NotifyLog($"ShowExhaustedToast [{serviceName}] ShowNear returned, IsVisible={toast.IsVisible} Left={toast.Left} Top={toast.Top}");
        }
        catch (Exception ex)
        {
            NotifyLog($"ShowExhaustedToast [{serviceName}] threw: {ex}");
        }
        if (_settings.NotifySoundEnabled) PlaySound(ref _tromboneSoundPlayer, "sad_trombone.wav");
        if (_settings.TelegramNotifyUsage) _ = _telegram.SendNotificationAsync(message);
    }

    private static void PlaySound(ref SoundPlayer? player, string fileName)
    {
        try
        {
            player ??= LoadSoundPlayer(fileName);
            player?.Play();
        }
        catch
        {
            // Best effort — a missing/broken sound device shouldn't affect anything else.
        }
    }

    private static SoundPlayer? LoadSoundPlayer(string fileName)
    {
        var uri = new Uri($"pack://application:,,,/ClaudeUsageTray;component/Assets/{fileName}");
        var streamInfo = Application.GetResourceStream(uri);
        return streamInfo is null ? null : new SoundPlayer(streamInfo.Stream);
    }

    private void UpdateTrayText()
    {
        var enabled = _providers.Where(IsEnabled).ToList();
        if (_settings.PopupMode == PopupMode.Rich)
        {
            // Windows always shows some tooltip bubble on hover for a
            // registered tray icon — an empty string just produces an empty
            // bubble, it doesn't suppress it. So this is the app name, not
            // usage data (the custom panel handles that).
            _trayIcon.ToolTipText = Strings.T("app.name");
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
