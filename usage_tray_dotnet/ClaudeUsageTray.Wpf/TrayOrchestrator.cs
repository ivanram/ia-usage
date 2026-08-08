using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using H.NotifyIcon;
using MaterialDesignThemes.Wpf;

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
    private readonly NotificationStateStore _notificationState = NotificationStateStore.Load();
    private static SoundPlayer? _chimePlayer;
    private static SoundPlayer? _tromboneSoundPlayer;
    private DateTime? _lastUpdated;

    private TaskbarIcon _trayIcon = null!;
    private readonly TrayMenuWindow _trayMenu = new();
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private int _hoverMs;
    private bool _hoverTriggered;
    private readonly PopupWindow _popup = new();
    private readonly TelegramBotService _telegram;
    private readonly UsageHistoryStore _historyStore = new();
    private readonly PromptCountStore _promptCountStore = new();
    private readonly PromptScanCache _promptScanCache = new();
    private readonly DispatcherTimer _promptCountTimer = new() { Interval = TimeSpan.FromMinutes(60) };
    // Slightly under the timer's own 60-minute cadence so normal jitter
    // never blocks a legitimate tick, while still catching the case that
    // actually matters: the app being closed and reopened a few times
    // within the same hour, which would otherwise cluster several
    // near-duplicate full-transcript scans together.
    private static readonly TimeSpan MinPromptSampleInterval = TimeSpan.FromMinutes(55);
    private StatsWindow? _statsWindow;
    private AppSettings _settings = AppSettings.Load();

    public TrayOrchestrator()
    {
        _telegram = new TelegramBotService(
            () => _providers.Where(IsEnabled)
                .Select(p => _lastSnapshots.TryGetValue(p.Name, out var s) ? s : new UsageSnapshot { ServiceName = p.Name, Ok = false, ErrorMessage = Strings.T("loading") }),
            _historyStore, _promptCountStore, _promptScanCache);
    }

    public void Start()
    {
        Strings.Current = _settings.Language;
        ApplyTheme();
        AutoStartHelper.SyncIfEnabled();

        PopulateTrayMenu();
        _trayMenu.WarmUp();

        _trayIcon = new TaskbarIcon
        {
            Id = TrayIconGuid,
            Icon = IconFactory.BuildRobotIcon(),
            ToolTipText = _settings.PopupMode == PopupMode.Rich ? Strings.T("app.name") : Strings.F("tray.tooltip.starting", Strings.T("app.name")),
        };
        _trayIcon.TrayLeftMouseUp += (s, e) => OnTrayLeftClick();
        _trayIcon.TrayRightMouseUp += (s, e) => _trayMenu.ShowNearCursor();
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
        _popup.CloseRequested += (s, e) => _popup.Hide();
        _popup.WarmUp();

        ApplyTelegramSettings();
        ApplyRefreshInterval();
        _refreshTimer.Tick += async (s, e) => await RefreshAllAsync();

        _promptCountTimer.Tick += async (s, e) => await SamplePromptCountsAsync();
        _promptCountTimer.Start();
        _ = SamplePromptCountsAsync();

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

    /// <summary>Re-renders the popup against the current snapshots/settings, but only if it's already on screen — a no-op cost check, never forces it open.</summary>
    private void RenderPopupIfVisible()
    {
        if (_popup.IsVisible) RenderPopup();
    }

    private void RenderPopup()
    {
        var enabled = _providers.Where(IsEnabled).ToList();
        var ready = enabled.Where(p => _lastSnapshots.ContainsKey(p.Name)).Select(p => _lastSnapshots[p.Name]);
        _popup.Render(ready, hasAnyEnabled: enabled.Count > 0, lastUpdated: _lastUpdated, totalEnabled: enabled.Count);
    }

    /// <summary>
    /// Settings' Apariencia card calls this live, as the user drags the
    /// style/opacity/blur controls — before they've hit Guardar. Bringing
    /// the panel up (pinned, so it doesn't auto-hide out from under them)
    /// is the whole point: there's no other way to actually judge how a
    /// blur/opacity choice looks against the real desktop behind it.
    /// Fire-and-forget: the caller is a synchronous UI event handler
    /// (slider drag, radio click) and PreviewStyleAsync's own await is only
    /// there to ride out DWM's blur-toggle fade — nothing here needs to
    /// block on it.
    /// </summary>
    private void PreviewAppearance(PopupWindowStyle style, int opacityPercent, int blurPercent, string accentColor)
    {
        // WPF's modal ShowDialog() disables every OTHER open window
        // (Settings' own has no owner set, so this includes the popup even
        // though it isn't Settings' child) for as long as the dialog is up.
        // A disabled AllowsTransparency window loses correct layered
        // compositing — showing a hard gray box where it should be
        // invisible — and Measure() against it can't be trusted either,
        // which is what made the panel open at the wrong size and only
        // snap right on the next render. Re-enabling it here undoes both.
        _popup.IsEnabled = true;

        var enabled = _providers.Where(IsEnabled).ToList();
        var ready = enabled.Where(p => _lastSnapshots.ContainsKey(p.Name)).Select(p => _lastSnapshots[p.Name]);
        // Same accent-sentinel handling as ApplyTheme() below — kept in
        // sync here so the bars' color previews live too, not just the
        // panel's base tint/blur.
        var flatBarColorHex = accentColor == AppSettings.OriginalAccentSentinel ? null : accentColor;

        var settings = _openSettingsWindow;
        _ = _popup.PreviewStyleAsync(
            style, opacityPercent, blurPercent, flatBarColorHex,
            ready, hasAnyEnabled: enabled.Count > 0, lastUpdated: _lastUpdated, totalEnabled: enabled.Count,
            besideLeft: settings?.Left, besideTop: settings?.Top, besideWidth: settings?.ActualWidth, besideHeight: settings?.ActualHeight);
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
    // icons, SettingsWindow's card headers).
    private const string MenuIconRefresh = "";
    private const string MenuIconSettings = "";
    private const string MenuIconAbout = "";
    private const string MenuIconExit = "";

    /// <summary>
    /// Rebuilds _trayMenu's rows in place — called at startup, again (via
    /// PreviewLanguage) whenever the language changes live, and after every
    /// refresh/login so the "Iniciar sesión" section only ever lists
    /// providers that still need it.
    /// </summary>
    private void PopulateTrayMenu()
    {
        _trayMenu.ClearItems();

        _trayMenu.AddItem(MenuIconRefresh, Strings.T("menu.refresh"), () => _ = RefreshAllAsync());
        _trayMenu.AddItem(MenuIconSettings, Strings.T("menu.settings"), OpenSettings);
        _trayMenu.AddItem(MenuIconAbout, Strings.T("menu.about"), OpenAbout);

        var loginProviders = _providers
            .Where(p => p.SupportsLogin && !(_lastSnapshots.TryGetValue(p.Name, out var s) && s.Ok))
            .ToList();
        if (loginProviders.Count > 0)
        {
            _trayMenu.AddLabel(Strings.T("menu.login"));
            foreach (var provider in loginProviders)
            {
                _trayMenu.AddItem("", provider.Name, () => _ = LoginAsync(provider), indented: true);
            }
        }

        _trayMenu.AddSeparator();
        _trayMenu.AddItem(MenuIconExit, Strings.T("menu.exit"), ExitApp);
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
            previewLanguage: PreviewLanguage,
            previewAppearance: PreviewAppearance,
            openAbout: OpenAbout);
        _openSettingsWindow = window;

        window.ShowDialog();
        _openSettingsWindow = null;

        if (!window.Saved)
        {
            ApplyTheme(); // revert any live theme/appearance preview back to the saved settings
            PreviewLanguage(_settings.Language); // same revert, for the language preview
            RenderPopupIfVisible(); // ApplyTheme() only resets the popup's fields, not its already-drawn visuals
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

    private AboutWindow? _openAboutWindow;

    /// <summary>Same show-or-activate singleton pattern as _openSettingsWindow — a second click just brings the existing window forward instead of opening a duplicate.</summary>
    private void OpenAbout()
    {
        if (_openAboutWindow is not null)
        {
            _openAboutWindow.Activate();
            return;
        }

        _openAboutWindow = new AboutWindow();
        _openAboutWindow.Closed += (s, e) => _openAboutWindow = null;
        _openAboutWindow.Show();
    }

    /// <summary>Applies a language process-wide and rebuilds anything with text already baked in at construction time — currently just the tray menu.</summary>
    private void PreviewLanguage(AppLanguage language)
    {
        Strings.Current = language;
        PopulateTrayMenu();
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

        _statsWindow = new StatsWindow(_providers.Where(IsEnabled).Select(p => p.Name).ToList(), _historyStore, _promptCountStore, _promptScanCache, anchor);
        _statsWindow.Closed += (s, e) => _statsWindow = null;
        _statsWindow.Show();
    }

    private void ApplyTheme()
    {
        ThemeHelper.Apply(_settings.Theme);
        ThemeHelper.ApplyAccent(_settings.AccentColor);
        _popup.FlatBarColorHex = _settings.AccentColor == AppSettings.OriginalAccentSentinel ? null : _settings.AccentColor;
        _popup.AnimationsEnabled = _settings.AnimationsEnabled;
        _popup.StyleMode = _settings.PopupWindowStyleMode;
        _popup.OpacityPercent = _settings.PopupOpacityPercent;
        _popup.BlurPercent = _settings.PopupBlurPercent;
        _trayMenu.ApplyTheme(new PaletteHelper().GetTheme().GetBaseTheme() == BaseTheme.Dark);
        _statsWindow?.RefreshTheme();
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
                PopulateTrayMenu();
                break;
            }
        }
    }

    /// <summary>
    /// Full-transcript scan feeding both _promptScanCache (dashboard cards,
    /// calendar view, /proyectos) and _promptCountStore (the Stats window
    /// chart's trend line), so this runs on its own 60-minute timer rather
    /// than alongside the usage refresh, and — just as importantly — rather
    /// than on every Stats window open or range-tab click, which used to
    /// redo this same scan live and peg the CPU.
    ///
    /// The scan itself always runs, on every call including the one at
    /// startup — _promptScanCache is memory-only, so it starts empty on
    /// every launch and has nothing else to populate it from. Only the
    /// _promptCountStore WRITE is still gated by ShouldSampleNow, to avoid
    /// clustering near-duplicate history rows if the app gets closed and
    /// reopened a few times in the same hour; skipping the scan entirely in
    /// that case (like this used to) left the cache permanently empty until
    /// the next real timer tick, up to 60 minutes later.
    /// </summary>
    private async Task SamplePromptCountsAsync()
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var (claudeCodeScan, codexScan) = await Task.Run(() =>
                (ClaudeCodeProjectsHelper.ScanPrompts(), CodexProjectsHelper.ScanPrompts()));

            _promptScanCache.Set("Claude Code", claudeCodeScan.TotalsByProject, claudeCodeScan.Timestamps);
            _promptScanCache.Set("Codex", codexScan.TotalsByProject, codexScan.Timestamps);
            _statsWindow?.OnPromptScanRefreshed();

            if (!_promptCountStore.ShouldSampleNow(MinPromptSampleInterval))
            {
                Log("SamplePromptCountsAsync: cache refreshed, DB snapshot skipped (sampled recently)");
                return;
            }

            _promptCountStore.RecordSnapshot("Claude Code", claudeCodeScan.TotalsByProject, now);
            _promptCountStore.RecordSnapshot("Codex", codexScan.TotalsByProject, now);
            Log($"SamplePromptCountsAsync: Claude Code {claudeCodeScan.TotalsByProject.Count} project(s), Codex {codexScan.TotalsByProject.Count} project(s)");
        }
        catch (Exception ex)
        {
            Log($"SamplePromptCountsAsync failed: {ex}");
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

        // Unconditional, not gated on _popup.IsVisible — the popup can be
        // shown/hidden (WarmUp's own startup Show()+Hide(), an away-hide
        // while a slow first fetch is still running, etc.) at any point
        // between this call and the matching one in `finally` below. Gating
        // both independently on IsVisible let them see different snapshots
        // of it, which could skip the "stop spinning" call entirely and
        // leave the refresh button spinning forever from then on.
        // SetRefreshing itself is a safe no-op on the visuals whenever the
        // button doesn't exist yet, so calling it unconditionally costs
        // nothing.
        _popup.SetRefreshing(true);
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
            PopulateTrayMenu();
        }
        finally
        {
            _popup.SetRefreshing(false);
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
    /// catching a future 0->100 crossing they might never see. That's the
    /// desired behavior for the desktop toast (subtle, once per launch is
    /// fine) but not for a Telegram push — repeating on every restart while
    /// still exhausted is genuinely annoying on a phone. Telegram gets its
    /// own decision below, driven by _notificationState (persisted to disk,
    /// so it survives restarts) instead of the in-memory flags.
    /// </summary>
    private void CheckForReset(string serviceName, UsageSnapshot snap)
    {
        var notifyEnabled = IsNotifyEnabled(serviceName);
        NotifyLog($"CheckForReset [{serviceName}] notifyEnabled={notifyEnabled} bars={string.Join(",", snap.Bars.Select(b => $"{b.Label}={b.Percent}%"))}");

        // Detection (and the reset-history log below) always runs, even
        // with notifications disabled for this service — the Stats window
        // marking when a service actually resets is a data question, not a
        // notification preference. Only whether to actually show/send
        // anything is gated on notifyEnabled, at the bottom.
        var resetDetected = false;
        var exhaustedDetected = false;
        var weeklyResetDetected = false;
        var telegramExhaustedWorthy = false;
        var telegramEightyWorthy = false;
        var stateChanged = false;
        foreach (var bar in snap.Bars)
        {
            var key = $"{serviceName}|{bar.Label}";
            if (_lastPercents.TryGetValue(key, out var prev))
            {
                if (prev >= 10 && bar.Percent < prev - 4)
                {
                    resetDetected = true;
                    // "Weekly" = whatever cadence the provider's own PRIMARY
                    // recurring quota bar resets on (see UsageBar.IsPrimary),
                    // never the short-window one (Claude's 5-hour limit) —
                    // that's the whole point of logging it: some providers
                    // reset early, and this is how you'd actually notice.
                    if (bar.IsPrimary) weeklyResetDetected = true;
                }
                // Fires once on the crossing into 100%, not on every
                // refresh that happens to still read above the line afterward.
                if (prev < 100 && bar.Percent >= 100) exhaustedDetected = true;
                NotifyLog($"  [{key}] prev={prev} now={bar.Percent} resetDetected={resetDetected} exhaustedDetected={exhaustedDetected}");
            }
            else
            {
                if (bar.Percent >= 100) exhaustedDetected = true;
                NotifyLog($"  [{key}] no previous reading, now={bar.Percent} exhaustedDetected={exhaustedDetected}");
            }
            _lastPercents[key] = bar.Percent;

            // Persisted level machine: "none" -> "eighty" -> "exhausted",
            // dropping back to "none" only on a real reset (bar < 80%).
            // Telegram only fires the first time a key climbs INTO a level
            // it wasn't already at, per the persisted state — restarting
            // the app just reloads the same stored level, so it can't
            // re-trigger a send on its own.
            var currentLevel = bar.Percent >= 100 ? "exhausted" : bar.Percent >= 80 ? "eighty" : "none";
            var storedLevel = _notificationState.Level.TryGetValue(key, out var lvl) ? lvl : "none";
            if (currentLevel != storedLevel)
            {
                if (currentLevel == "exhausted" && storedLevel != "exhausted") telegramExhaustedWorthy = true;
                if (currentLevel == "eighty" && storedLevel == "none") telegramEightyWorthy = true;
                _notificationState.Level[key] = currentLevel;
                stateChanged = true;
            }
        }
        if (stateChanged) _notificationState.Save();
        if (weeklyResetDetected) _historyStore.RecordReset(serviceName, DateTimeOffset.UtcNow);

        NotifyLog($"CheckForReset [{serviceName}] result: resetDetected={resetDetected} exhaustedDetected={exhaustedDetected} weeklyResetDetected={weeklyResetDetected} telegramExhaustedWorthy={telegramExhaustedWorthy} telegramEightyWorthy={telegramEightyWorthy}");

        if (!notifyEnabled) return;

        if (resetDetected)
        {
            ShowResetToast(serviceName);
            // A reset can only ever be detected from a live in-session delta
            // (see the doc comment above), so unlike exhausted/eighty this
            // is already restart-safe without needing the persisted state.
            if (_settings.TelegramNotifyUsage) _ = _telegram.SendNotificationAsync(Strings.F("toast.reset", serviceName));
        }
        if (exhaustedDetected) ShowExhaustedToast(serviceName);
        if (telegramExhaustedWorthy && _settings.TelegramNotifyUsage)
        {
            _ = _telegram.SendNotificationAsync(Strings.F("toast.exhausted", serviceName));
        }
        // 80% is a Telegram-only heads-up — no desktop toast/sound for it,
        // matching the user's ask for a lighter-weight "just so you know"
        // separate from the more attention-grabbing reset/exhausted ones.
        if (telegramEightyWorthy && _settings.TelegramNotifyUsage && _settings.TelegramNotify80Percent)
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
        _promptCountTimer.Stop();
        _telegram.Stop();
        foreach (var host in _hosts.Values)
        {
            host.CloseForReal();
        }
    }
}
