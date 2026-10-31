using ListenTeams.Core;
using ListenTeams.Core.Apps;
using ListenTeams.Core.Output;
using ListenTeams.Core.Settings;
using ListenTeams.Core.Teams;
using ListenTeams.Forms;
using ListenTeams.Platform;

namespace ListenTeams;

/// <summary>The notification-area icon and its menu. ListenTeams has no main window.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly SettingsStore _settings;
    private readonly CallCoordinator _coordinator;
    private readonly NotifyIcon _icon;
    private readonly SynchronizationContext _ui;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _detailItem;
    private readonly ToolStripMenuItem _recordItem;
    private readonly ToolStripMenuItem _recentItem;
    private readonly ToolStripMenuItem _appsItem;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private string? _balloonPath;
    private TrayState? _shownState;
    private bool _exiting;

    public TrayApp(SettingsStore settings, bool startedInBackground, bool firstRun)
    {
        _settings = settings;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _coordinator = new CallCoordinator(settings);

        _statusItem = new ToolStripMenuItem("Démarrage…") { Enabled = false };
        _detailItem = new ToolStripMenuItem { Enabled = false, Visible = false };
        _recordItem = new ToolStripMenuItem("Enregistrer maintenant", null, (_, _) => ToggleRecording());
        _recentItem = new ToolStripMenuItem("Appels récents");
        _recentItem.DropDownItems.Add(new ToolStripMenuItem("(aucun)") { Enabled = false });

        // Choice of the applications to record: Teams, Discord or both.
        _appsItem = new ToolStripMenuItem("Applications à enregistrer");
        foreach (var info in CallApps.All)
        {
            var app = info.App;
            var item = new ToolStripMenuItem(info.App == CallApp.Teams ? "Microsoft Teams" : info.DisplayName) { CheckOnClick = true };
            item.CheckedChanged += (_, _) =>
            {
                if (_settings.Current.IsEnabled(app) != item.Checked) _settings.Update(s => s.SetEnabled(app, item.Checked));
            };
            _appsItem.DropDownItems.Add(item);
        }

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _statusItem,
            _detailItem,
            new ToolStripSeparator(),
            _recordItem,
            new ToolStripMenuItem("Ouvrir le dernier compte rendu", null, (_, _) => OpenLatestReport()),
            _recentItem,
            new ToolStripMenuItem("Ouvrir le dossier des enregistrements", null, (_, _) => OpenOutputFolder()),
            new ToolStripSeparator(),
            _appsItem,
            new ToolStripMenuItem("Connexion à Teams…", null, (_, _) => ShowTeamsConnection()),
            new ToolStripMenuItem("Paramètres…", null, (_, _) => ShowSettings()),
            new ToolStripMenuItem("Journaux", null, (_, _) => AppPaths.Open(AppPaths.LogsFolder)),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Quitter ListenTeams", null, async (_, _) => await ExitAsync()),
        ]);
        menu.Opening += (_, _) =>
        {
            RefreshRecentCalls();
            for (int i = 0; i < CallApps.All.Count; i++)
            {
                ((ToolStripMenuItem)_appsItem.DropDownItems[i]).Checked = _settings.Current.IsEnabled(CallApps.All[i].App);
            }
        };

        _icon = new NotifyIcon
        {
            Icon = TrayIcons.For(TrayState.Idle),
            Text = "ListenTeams",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenLatestReport();
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_balloonPath is not null) AppPaths.Open(_balloonPath);
        };

        _coordinator.StatusChanged += status => _ui.Post(_ => ShowStatus(status), null);
        _coordinator.Notification += notification => _ui.Post(_ => ShowNotification(notification), null);

        // Refreshes the elapsed time while the menu is open.
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _refreshTimer.Tick += (_, _) => ShowStatus(_coordinator.Status);
        _refreshTimer.Start();

        _coordinator.Start();

        if (firstRun)
        {
            ShowNotification(new UserNotification(
                "ListenTeams est installé",
                $"Il enregistrera automatiquement vos appels {EnabledAppsText(_settings.Current)}. Configurez la synthèse dans les paramètres."));
            ShowSettings();
        }
        else if (!startedInBackground)
        {
            ShowNotification(new UserNotification(
                "ListenTeams est actif",
                $"Il attend vos appels {EnabledAppsText(_settings.Current)} dans la zone de notification (près de l'horloge)."));
        }
    }

    private void ShowStatus(CoordinatorStatus status)
    {
        string main;
        string? detail = null;
        string watched = status.EnabledApps.Count == 0
            ? "aucune application"
            : string.Join(" ou ", status.EnabledApps.Select(CallApps.Name));
        if (status.RecordingApp is { } app)
        {
            string name = CallApps.Name(app);
            main = $"● Enregistrement {name} — {status.Elapsed:hh\\:mm\\:ss}";
            detail = status.Mute switch
            {
                { Source: MuteSource.Unknown, Muted: true } => $"Micro : état {name} inconnu, votre voix n'est pas enregistrée",
                { Source: MuteSource.Unknown } => $"Micro : état {name} inconnu, votre voix est enregistrée",
                { Muted: true } => $"Micro coupé dans {name} : votre voix n'est pas enregistrée",
                _ => $"Micro actif dans {name} : votre voix est enregistrée",
            };
        }
        else if (status.EnabledApps.Count == 0)
        {
            main = "Aucune application choisie (menu Applications à enregistrer)";
        }
        else if (status.RunningApps.Count > 0)
        {
            main = $"En attente d'un appel {watched}";
        }
        else
        {
            main = status.EnabledApps.Count == 1
                ? $"{watched} n'est pas lancé"
                : $"{string.Join(" et ", status.EnabledApps.Select(CallApps.Name))} ne sont pas lancés";
        }

        if (status.ProcessingText is not null || status.ProcessingQueueLength > 0)
        {
            string processing = status.ProcessingText ?? "Traitement en attente…";
            if (status.ProcessingQueueLength > 1) processing += $" ({status.ProcessingQueueLength} appels)";
            detail = detail is null ? processing : $"{detail} · {processing}";
        }

        _statusItem.Text = main;
        _detailItem.Text = detail ?? string.Empty;
        _detailItem.Visible = detail is not null;
        _recordItem.Text = status.Recording ? "Arrêter l'enregistrement" : "Enregistrer maintenant";

        if (_shownState != status.State)
        {
            _shownState = status.State;
            _icon.Icon = TrayIcons.For(status.State);
        }

        string tooltip = "ListenTeams — " + main;
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

    private static string EnabledAppsText(AppSettings settings)
    {
        var names = CallApps.All.Where(a => settings.IsEnabled(a.App)).Select(a => a.DisplayName).ToList();
        return names.Count == 0 ? "Teams et Discord" : string.Join(" et ", names);
    }

    private void ShowNotification(UserNotification notification)
    {
        if (_exiting) return;
        _balloonPath = notification.OpenPath;
        _icon.ShowBalloonTip(5000, notification.Title, notification.Text, notification.Icon);
    }

    private void ToggleRecording()
    {
        if (_coordinator.Status.Recording) _coordinator.StopManually();
        else _coordinator.StartManually();
    }

    private void RefreshRecentCalls()
    {
        _recentItem.DropDownItems.Clear();
        var calls = CallFolder.List(_settings.Current.OutputFolder, 10).ToList();
        if (calls.Count == 0)
        {
            _recentItem.DropDownItems.Add(new ToolStripMenuItem("(aucun)") { Enabled = false });
            return;
        }

        foreach (var (folder, metadata) in calls)
        {
            string state = metadata.Status switch
            {
                CallStatus.Done => string.Empty,
                CallStatus.Failed => " — échec",
                CallStatus.Recording => " — en cours",
                _ => " — en traitement",
            };
            string label = $"{metadata.StartedAt:dd/MM HH:mm}  {metadata.Title}{state}";
            var path = File.Exists(folder.ReportPath) ? folder.ReportPath : folder.Path;
            _recentItem.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => AppPaths.Open(path)));
        }
    }

    private void OpenLatestReport()
    {
        var latest = CallFolder.List(_settings.Current.OutputFolder, 20)
            .FirstOrDefault(c => File.Exists(c.Folder.ReportPath));
        if (latest.Folder is not null) AppPaths.Open(latest.Folder.ReportPath);
        else OpenOutputFolder();
    }

    private void OpenOutputFolder()
    {
        Directory.CreateDirectory(_settings.Current.OutputFolder);
        AppPaths.Open(_settings.Current.OutputFolder);
    }

    private SettingsForm? _settingsForm;

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(_settings);
        _settingsForm.Show();
    }

    private void ShowTeamsConnection()
    {
        using var form = new TeamsConnectionForm(_coordinator);
        form.ShowDialog();
    }

    private async Task ExitAsync()
    {
        if (_coordinator.Status.Recording)
        {
            var answer = MessageBox.Show(
                "Un appel est en cours d'enregistrement. Quitter arrêtera l'enregistrement (le fichier sera conservé et traité au prochain lancement).\n\nQuitter quand même ?",
                "ListenTeams", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
        }

        _exiting = true;
        _refreshTimer.Stop();
        _icon.Text = "ListenTeams — fermeture…";
        await _coordinator.DisposeAsync();
        _icon.Visible = false;
        _icon.Dispose();
        Log.Info("ListenTeams fermé.");
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
