using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ClaudeUsageTray;

public partial class App : Application
{
    private TrayOrchestrator? _orchestrator;
    private static readonly string CrashLog = Path.Combine(AppContext.BaseDirectory, "crash.txt");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
