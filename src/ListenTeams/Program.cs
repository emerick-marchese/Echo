using ListenTeams.Core;
using ListenTeams.Core.Settings;
using ListenTeams.Platform;

namespace ListenTeams;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);

        using var mutex = new Mutex(true, @"Local\ListenTeams.SingleInstance", out bool firstInstance);
        if (!firstInstance)
        {
            if (!background)
            {
                MessageBox.Show(
                    "ListenTeams est déjà lancé : son icône se trouve dans la zone de notification, près de l'horloge.",
                    "ListenTeams", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Log.Initialize(AppPaths.LogsFolder);
        Log.Info($"ListenTeams {typeof(Program).Assembly.GetName().Version} démarré (Windows {Environment.OSVersion.Version}).");

        Application.ThreadException += (_, e) => Log.Error("Erreur non gérée (interface)", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Erreur non gérée", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Tâche en erreur non observée", e.Exception);
            e.SetObserved();
        };

        bool firstRun = !File.Exists(AppPaths.SettingsFile);
        var settings = new SettingsStore(AppPaths.SettingsFile, new DpapiProtector());
        if (firstRun)
        {
            // Persist defaults so the next start is not considered a first run.
            settings.Update(_ => { });
        }
        InstallerChoices.Apply(settings);

        Application.Run(new TrayApp(settings, background, firstRun));
    }
}
