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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, "ClaudeUsageTray-SingleInstance-8f3b6b3a-9e0e-4b7a-9c2f-2b7b6e6b6a1a", out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

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
