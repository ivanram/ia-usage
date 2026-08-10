using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace ClaudeUsageTray;

public partial class App : Application
{
    private TrayOrchestrator? _orchestrator;
    private static readonly string CrashLog = Path.Combine(Paths.LogsDir, "crash.txt");

    /// <summary>
    /// Deliberately placed next to the exe rather than under %LOCALAPPDATA%
    /// like every other log (see Paths.cs's doc comment on why logs moved
    /// OUT of that folder) — this one exists specifically for the "a friend
    /// downloaded it and nothing happens, no window, no process" report,
    /// where the person hitting it has no idea AppData exists and just
    /// wants to send back whatever's sitting next to the exe they double-
    /// clicked. Overwritten (not appended) at the start of every launch, so
    /// it always reflects only the most recent attempt. If the process gets
    /// killed before managed code even runs (SmartScreen, AV, a corrupted
    /// download), nothing can write this file at all — that itself is a
    /// useful (if silent) data point: no file appearing means the block
    /// happened before our code ever ran.
    /// </summary>
    private static readonly string StartupTraceLog = Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath!) ?? AppContext.BaseDirectory,
        "diagnostico_inicio.txt");

    private static void TraceStartup(string msg)
    {
        try { File.AppendAllText(StartupTraceLog, $"{DateTime.Now:O} {msg}\n"); } catch { /* best effort */ }
    }

    // Held for the app's whole lifetime — releasing/GC'ing it would let a
    // second instance start. The GUID just needs to be fixed and unique to
    // this app; it isn't a secret.
    private static Mutex? _singleInstanceMutex;

    // A named kernel event a manual dev/deploy step can Set() from outside
    // the process to ask this exact running instance to shut down cleanly —
    // same OnExit/Dispose path as the tray menu's "Salir", so WebView2 gets
    // to flush its cookie stores properly instead of losing a session to a
    // hard kill. Not part of the GitHub-Releases auto-update flow (that one
    // already replaces itself via TryHandleApplyUpdate) — this is purely
    // for installing a freshly-built exe onto this machine without the user
    // having to close/reopen the app by hand each time.
    private const string ExitSignalName = "ClaudeUsageTray-ExitSignal-8f3b6b3a-9e0e-4b7a-9c2f-2b7b6e6b6a1a";
    private EventWaitHandle? _exitSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        try { File.WriteAllText(StartupTraceLog, ""); } catch { /* best effort */ }
        TraceStartup($"OnStartup entered. Args=[{string.Join(" ", e.Args)}] Version={System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");

        try
        {
            base.OnStartup(e);
            TraceStartup("base.OnStartup done");

            // This launch might be the temp-downloaded new build finishing an
            // update on the old instance's behalf — that has to run even while
            // the old process (and its single-instance mutex) is still alive,
            // so it's handled before the mutex check below, not after.
            if (UpdateService.TryHandleApplyUpdate(e.Args))
            {
                TraceStartup("TryHandleApplyUpdate handled this launch as an update-apply step, shutting down");
                Shutdown();
                return;
            }

            _singleInstanceMutex = new Mutex(true, "ClaudeUsageTray-SingleInstance-8f3b6b3a-9e0e-4b7a-9c2f-2b7b6e6b6a1a", out var createdNew);
            TraceStartup($"Single-instance mutex acquired: createdNew={createdNew}");
            if (!createdNew)
            {
                TraceStartup("Another instance is already running, shutting down");
                Shutdown();
                return;
            }

            _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitSignalName);
            var dispatcher = Dispatcher;
            new Thread(() =>
            {
                _exitSignal.WaitOne();
                dispatcher.Invoke(Shutdown);
            }) { IsBackground = true, Name = "ExitSignalWatcher" }.Start();

            DispatcherUnhandledException += (s, ex) =>
            {
                var text = $"{DateTime.Now:O}\nDispatcherUnhandledException:\n{ex.Exception}\n";
                File.WriteAllText(CrashLog, text);
                TraceStartup(text);
                ex.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                var text = $"{DateTime.Now:O}\nAppDomain UnhandledException:\n{ex.ExceptionObject}\n";
                File.WriteAllText(CrashLog, text);
                TraceStartup(text);
            };
            TaskScheduler.UnobservedTaskException += (s, ex) =>
            {
                var text = $"{DateTime.Now:O}\nUnobservedTaskException:\n{ex.Exception}\n";
                File.WriteAllText(CrashLog, text);
                TraceStartup(text);
                ex.SetObserved();
            };
            TraceStartup("Exception handlers wired");

            _orchestrator = new TrayOrchestrator();
            TraceStartup("TrayOrchestrator constructed");
            _orchestrator.Start();
            TraceStartup("TrayOrchestrator.Start() returned — tray icon should be visible now");
            _ = UpdateService.CheckAndPromptAsync();
        }
        catch (Exception ex)
        {
            File.WriteAllText(CrashLog, $"{DateTime.Now:O}\nStartup exception:\n{ex}\n");
            TraceStartup($"FATAL during startup:\n{ex}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _orchestrator?.Dispose();
        base.OnExit(e);
    }
}
