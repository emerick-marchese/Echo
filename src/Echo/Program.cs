using Echo.Core;
using Echo.Core.Settings;
using Echo.Platform;

namespace Echo;

internal static class Program
{
    private const string ShowEventName = @"Local\Echo.ShowWindow";

    [STAThread]
    private static void Main(string[] args)
    {
        bool background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);

        using var mutex = new Mutex(true, @"Local\Echo.SingleInstance", out bool firstInstance);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!firstInstance)
        {
            // Echo is already running: bring its window to the front.
            if (!background) showEvent.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        Log.Initialize(AppPaths.LogsFolder);
        Log.Info($"Echo {typeof(Program).Assembly.GetName().Version} démarré (Windows {Environment.OSVersion.Version}).");

        Application.ThreadException += (_, e) => Log.Error("Erreur non gérée (interface)", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Erreur non gérée", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Tâche en erreur non observée", e.Exception);
            e.SetObserved();
        };

        LegacyMigration.Run();
        bool firstRun = !File.Exists(AppPaths.SettingsFile);
        var settings = new SettingsStore(AppPaths.SettingsFile, new DpapiProtector());
        if (firstRun)
        {
            // Persist defaults so the next start is not considered a first run.
            settings.Update(_ => { });
        }

        var app = new TrayApp(settings, background);
        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => ui.Post(_ => app.ShowWindow(), null), null, Timeout.Infinite, executeOnlyOnce: false);
        Application.Run(app);
    }
}
