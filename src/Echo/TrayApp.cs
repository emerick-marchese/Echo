using Echo.Core;
using Echo.Core.Apps;
using Echo.Core.Settings;
using Echo.Core.Teams;
using Echo.Platform;
using Echo.Ui;

namespace Echo;

/// <summary>
/// Runs Echo: the main window, and the notification-area icon that keeps Echo available
/// (and recording) when the window is closed.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly SettingsStore _settings;
    private readonly CallCoordinator _coordinator;
    private readonly MainWindow _window;
    private readonly NotifyIcon _icon;
    private readonly SynchronizationContext _ui;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _recordItem;
    private string? _balloonPath;
    private TrayState? _shownState;
    private bool _hintShown;
    private bool _exiting;

    public TrayApp(SettingsStore settings, bool startedInBackground)
    {
        _settings = settings;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _coordinator = new CallCoordinator(settings);
        _window = new MainWindow(settings, _coordinator);
        _window.VisibleChanged += (_, _) =>
        {
            if (!_window.Visible && !_exiting && !_hintShown)
            {
                _hintShown = true;
                ShowBalloon(new UserNotification("Echo reste actif", "Echo continue en arrière-plan : double-cliquez sur son icône pour le rouvrir."));
            }
        };

        _statusItem = new ToolStripMenuItem("Démarrage…") { Enabled = false };
        _recordItem = new ToolStripMenuItem("Démarrer l'enregistrement", null, async (_, _) => await ToggleRecordingAsync());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            new ToolStripMenuItem("Ouvrir Echo", null, (_, _) => _window.ShowAndActivate()) { Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold) },
            new ToolStripSeparator(),
            _statusItem,
            _recordItem,
            new ToolStripMenuItem("Enregistrements", null, (_, _) => _window.ShowAndActivate("recordings")),
            new ToolStripMenuItem("Paramètres", null, (_, _) => _window.ShowAndActivate("settings")),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Quitter Echo", null, async (_, _) => await ExitAsync()),
        ]);

        _icon = new NotifyIcon
        {
            Icon = TrayIcons.For(TrayState.Idle),
            Text = "Echo",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => _window.ShowAndActivate();
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_balloonPath is not null) AppPaths.Open(_balloonPath);
            else _window.ShowAndActivate();
        };

        _coordinator.StatusChanged += status => _ui.Post(_ => ShowStatus(status), null);
        _coordinator.Notification += notification => _ui.Post(_ => ShowNotification(notification), null);
        _coordinator.Start();

        if (!startedInBackground) _window.ShowAndActivate();
    }

    /// <summary>Called when Echo is launched again while already running.</summary>
    public void ShowWindow() => _window.ShowAndActivate();

    private void ShowStatus(CoordinatorStatus status)
    {
        string selected = CallApps.Name(_settings.Current.SelectedApp);
        string main;
        if (status.RecordingApp is { } app)
        {
            string name = CallApps.Name(app);
            string mic = status.Mute switch
            {
                { Source: MuteSource.Unknown } => "micro : état inconnu",
                { Muted: true } => "micro coupé",
                _ => "micro actif",
            };
            main = $"● Enregistrement {name} — {status.Elapsed:hh\\:mm\\:ss} ({mic})";
            _recordItem.Text = "Arrêter l'enregistrement";
        }
        else
        {
            main = status.ProcessingQueueLength > 0
                ? status.ProcessingText ?? "Traitement en cours…"
                : status.IsRunning(_settings.Current.SelectedApp) ? $"Prêt — {selected} ouvert" : $"Prêt — {selected} fermé";
            _recordItem.Text = $"Démarrer l'enregistrement {selected}";
        }
        _statusItem.Text = main;

        if (_shownState != status.State)
        {
            _shownState = status.State;
            _icon.Icon = TrayIcons.For(status.State);
        }

        string tooltip = "Echo — " + main;
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

    private void ShowNotification(UserNotification notification)
    {
        if (_exiting) return;
        // The window shows its own notifications when it is in front.
        if (_window.IsActiveWindow) _window.Notify(notification);
        else ShowBalloon(notification);
    }

    private void ShowBalloon(UserNotification notification)
    {
        _balloonPath = notification.OpenPath;
        _icon.ShowBalloonTip(5000, notification.Title, notification.Text, notification.Icon);
    }

    private async Task ToggleRecordingAsync()
    {
        if (_coordinator.Status.Recording) await _coordinator.StopRecordingAsync();
        else await _coordinator.StartRecordingAsync(_settings.Current.SelectedApp);
    }

    private async Task ExitAsync()
    {
        if (_coordinator.Status.Recording)
        {
            var answer = MessageBox.Show(
                "Un appel est en cours d'enregistrement. Quitter arrêtera l'enregistrement (le fichier sera conservé et traité au prochain lancement).\n\nQuitter quand même ?",
                "Echo", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
        }

        _exiting = true;
        _icon.Text = "Echo — fermeture…";
        _window.AllowClose = true;
        _window.Close();
        await _coordinator.DisposeAsync();
        _icon.Visible = false;
        _icon.Dispose();
        Log.Info("Echo fermé.");
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Dispose();
            _window.Dispose();
        }
        base.Dispose(disposing);
    }
}
