using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace ClaudeUsageTray;

public partial class App : Application
{
    private TrayOrchestrator? _orchestrator;
    private static readonly string CrashLog = Path.Combine(Paths.LogsDir, "crash.txt");

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
        base.OnStartup(e);

        // This launch might be the temp-downloaded new build finishing an
        // update on the old instance's behalf — that has to run even while
        // the old process (and its single-instance mutex) is still alive,
        // so it's handled before the mutex check below, not after.
        if (UpdateService.TryHandleApplyUpdate(e.Args))
        {
            Shutdown();
            return;
        }

        _singleInstanceMutex = new Mutex(true, "ClaudeUsageTray-SingleInstance-8f3b6b3a-9e0e-4b7a-9c2f-2b7b6e6b6a1a", out var createdNew);
        if (!createdNew)
        {
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
            File.WriteAllText(CrashLog, $"{DateTime.Now:O}\nDispatcherUnhandledException:\n{ex.Exception}\n");
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
        {
            File.WriteAllText(CrashLog, $"{DateTime.Now:O}\nAppDomain UnhandledException:\n{ex.ExceptionObject}\n");
        };
        TaskScheduler.UnobservedTaskException += (s, ex) =>
        {
            File.WriteAllText(CrashLog, $"{DateTime.Now:O}\nUnobservedTaskException:\n{ex.Exception}\n");
            ex.SetObserved();
        };

        try
        {
            _orchestrator = new TrayOrchestrator();
            _orchestrator.Start();
            _ = UpdateService.CheckAndPromptAsync();
        }
        catch (Exception ex)
        {
            File.WriteAllText(CrashLog, $"{DateTime.Now:O}\nStartup exception:\n{ex}\n");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _orchestrator?.Dispose();
        base.OnExit(e);
    }
}
