using System.Collections.Concurrent;
using ListenTeams.Core;
using ListenTeams.Core.Apps;
using ListenTeams.Core.Output;
using ListenTeams.Core.Settings;
using ListenTeams.Core.Teams;
using ListenTeams.Platform;

namespace ListenTeams;

internal sealed record CoordinatorStatus(
    TrayState State,
    IReadOnlyList<CallApp> EnabledApps,
    IReadOnlyList<CallApp> RunningApps,
    CallApp? RecordingApp,
    bool Manual,
    TimeSpan Elapsed,
    MuteDecision? Mute,
    TeamsApiState Api,
    bool ApiPaired,
    string? ProcessingText,
    int ProcessingQueueLength)
{
    public bool Recording => RecordingApp is not null;

    public bool TeamsRunning => RunningApps.Contains(CallApp.Teams);
}

internal sealed record UserNotification(string Title, string Text, ToolTipIcon Icon = ToolTipIcon.Info, string? OpenPath = null);

/// <summary>
/// Watches the calling applications chosen by the user (Teams, Discord), starts and stops
/// recordings, keeps the microphone gate in sync with the mute state of the application,
/// and hands finished calls to the processing queue. One call is recorded at a time.
/// All the logic runs on one background loop (one tick per second).
/// </summary>
internal sealed class CallCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan MinimumCallLength = TimeSpan.FromSeconds(8);

    /// <summary>Detection state of one calling application.</summary>
    private sealed class AppWatch(CallApp app)
    {
        public CallApp App { get; } = app;

        public CallDetector Detector { get; } = new();

        public bool Enabled { get; set; }

        public IReadOnlyList<int> Roots { get; set; } = [];

        public HashSet<int> AllProcesses { get; set; } = [];

        public bool Running => Roots.Count > 0;
    }

    private readonly SettingsStore _settings;
    private readonly IReadOnlyList<AppWatch> _watches = CallApps.All.Select(a => new AppWatch(a.App)).ToList();
    private readonly TeamsLocalApiClient _api;
    private readonly MeetingTitleTracker _titles = new();
    private readonly ProcessingQueue _processing;
    private readonly ConcurrentQueue<Func<Task>> _commands = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<MuteSource> _muteSourcesUsed = [];
    private readonly object _muteLock = new();

    private volatile CallRecorder? _recorder;
    private volatile AppWatch? _active;
    private CallWindowMuteReader? _windowReader;
    private CallFolder? _folder;
    private CallMetadata? _metadata;
    private MuteDecision? _mute;
    private DateTime _lastTitleUpdate;
    private bool _warnedUnknownMute;
    private string? _processingText;
    private Task? _loop;
    private Task? _muteLoop;
    private Task? _apiLoop;

    public CallCoordinator(SettingsStore settings)
    {
        _settings = settings;
        _api = new TeamsLocalApiClient(
            () => _settings.Current.TeamsApiToken,
            token => _settings.Update(s => s.TeamsApiToken = token),
            typeof(CallCoordinator).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
        _api.Paired += () => RaiseNotification(new UserNotification(
            "ListenTeams est connecté à Teams",
            "L'état de votre micro dans Teams est maintenant suivi précisément."));

        // The API pushes mute changes: apply them right away.
        _api.StateChanged += _ =>
        {
            try
            {
                ApplyMute();
            }
            catch (Exception ex)
            {
                Log.Error("Erreur en appliquant l'état du micro Teams", ex);
            }
        };

        _processing = new ProcessingQueue(settings);
        _processing.ProgressChanged += text =>
        {
            _processingText = text;
            PublishStatus();
        };
        _processing.Completed += result => RaiseNotification(new UserNotification(
            "Compte rendu prêt",
            result.SummaryError is null
                ? $"{result.Metadata.Title} — cliquez pour l'ouvrir."
                : $"{result.Metadata.Title} — transcription prête, synthèse indisponible : {result.SummaryError}",
            result.SummaryError is null ? ToolTipIcon.Info : ToolTipIcon.Warning,
            result.Folder.ReportPath));
        _processing.Failed += (folder, ex) => RaiseNotification(new UserNotification(
            "Échec du traitement de l'appel",
            $"{ex.Message} (l'enregistrement audio est conservé).",
            ToolTipIcon.Error,
            folder.Path));
    }

    public event Action<CoordinatorStatus>? StatusChanged;

    public event Action<UserNotification>? Notification;

    public CoordinatorStatus Status { get; private set; } = new(
        TrayState.Idle, [], [], null, false, TimeSpan.Zero, null, TeamsApiState.Disconnected, false, null, 0);

    private AppWatch Teams => _watches.First(w => w.App == CallApp.Teams);

    public void Start()
    {
        _processing.Start(_cts.Token);
        foreach (var folder in CallFolder.FindUnfinished(_settings.Current.OutputFolder))
        {
            Log.Info($"Reprise du traitement interrompu : {folder.Path}");
            _processing.Enqueue(folder);
        }

        _apiLoop = Task.Run(() => _api.RunAsync(() => Teams.Enabled && Teams.Running, _cts.Token));
        _loop = Task.Run(LoopAsync);
        _muteLoop = Task.Run(MuteLoopAsync);
    }

    /// <summary>
    /// Records now, even without a detected call: the application in a call, otherwise the
    /// first enabled application that is running, otherwise the microphone alone.
    /// </summary>
    public void StartManually() => _commands.Enqueue(async () =>
    {
        if (_active is not null) return;
        var enabled = _watches.Where(w => w.Enabled).ToList();
        var watch = enabled.FirstOrDefault(w => w.Detector.CallInProgress)
                    ?? enabled.FirstOrDefault(w => w.Running)
                    ?? enabled.FirstOrDefault()
                    ?? Teams;
        if (watch.Detector.StartManually() == CallEvent.Started)
        {
            await StartRecordingAsync(watch).ConfigureAwait(false);
        }
    });

    public void StopManually() => _commands.Enqueue(async () =>
    {
        if (_active is { } watch && watch.Detector.StopManually() == CallEvent.Ended)
        {
            await StopRecordingAsync().ConfigureAwait(false);
        }
    });

    /// <summary>Asks Teams to show its "allow ListenTeams" prompt (only works during a meeting).</summary>
    public Task<bool> RequestPairingAsync() => _api.RequestPairingAsync();

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await TickAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Erreur dans la surveillance des appels", ex);
            }

            try
            {
                await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync()
    {
        while (_commands.TryDequeue(out var command))
        {
            await command().ConfigureAwait(false);
        }

        var settings = _settings.Current;
        var processes = AppProcesses.Snapshot.Take();
        var now = DateTime.UtcNow;

        foreach (var watch in _watches)
        {
            watch.Enabled = settings.IsEnabled(watch.App);
            watch.Roots = watch.Enabled ? processes.RootProcessIds(watch.App) : [];
            watch.AllProcesses = watch.Running ? processes.AllProcessIds(watch.App) : [];

            var detector = watch.Detector;
            detector.SetAppRunning(watch.Running);
            detector.SetApiState(watch.App == CallApp.Teams ? _api.State : TeamsApiState.Disconnected);
            detector.SetMicInUse(watch.Running && MicrophoneUsageMonitor.IsUsingMicrophone(watch.App));

            if (_active == watch && !watch.Enabled && !detector.IsManual)
            {
                // The user unticked this application during its call.
                detector.StopManually();
                await StopRecordingAsync().ConfigureAwait(false);
                continue;
            }

            switch (detector.Tick(now))
            {
                case CallEvent.Started when _active is null:
                    await StartRecordingAsync(watch).ConfigureAwait(false);
                    break;
                case CallEvent.Started:
                    // Already recording another application's call: one call at a time.
                    Log.Info($"Appel {CallApps.Name(watch.App)} ignoré : un appel {CallApps.Name(_active!.App)} est déjà enregistré.");
                    detector.StopManually();
                    break;
                case CallEvent.Ended when _active == watch:
                    await StopRecordingAsync().ConfigureAwait(false);
                    break;
            }
        }

        if (_active is { } active && _recorder is not null)
        {
            await UpdateRecordingAsync(active, settings).ConfigureAwait(false);
            if (active.App == CallApp.Teams) await MaybeRequestPairingAsync().ConfigureAwait(false);
        }

        PublishStatus();
    }

    private async Task StartRecordingAsync(AppWatch watch)
    {
        var settings = _settings.Current;
        var startedAt = DateTime.Now;
        string appName = CallApps.Name(watch.App);
        try
        {
            Directory.CreateDirectory(settings.OutputFolder);
            _folder = CallFolder.Create(settings.OutputFolder, startedAt);
            _metadata = new CallMetadata
            {
                App = appName,
                Title = CallMetadata.DefaultTitleFor(appName),
                StartedAt = startedAt,
                Status = CallStatus.Recording,
            };
            _folder.WriteMetadata(_metadata);

            _titles.OnCallStarted(watch.AllProcesses);
            _lastTitleUpdate = DateTime.MinValue;
            _warnedUnknownMute = false;
            MuteDecision initialMute;
            lock (_muteLock)
            {
                _muteSourcesUsed.Clear();
                _windowReader = new CallWindowMuteReader(watch.App);
                _active = watch;
                initialMute = ResolveMute(watch, settings);
                _mute = initialMute;
                _muteSourcesUsed.Add(initialMute.Source);
            }

            var options = new RecorderOptions(settings.OpusBitrate, settings.MicrophoneDeviceId, settings.EchoCancellation, settings.MuteLookBackMs);
            _recorder = await CallRecorder.StartAsync(_folder.AudioPath, _folder.EnvelopePath, _metadata.Title, options, watch.Roots, initialMute.Muted).ConfigureAwait(false);
            Log.Info($"Enregistrement {appName} démarré ({(watch.Detector.IsManual ? "manuel" : "automatique")}) : {_folder.Path}");

            if (settings.NotifyOnCallStart)
            {
                RaiseNotification(new UserNotification(
                    $"Enregistrement de l'appel {appName}",
                    _recorder.CapturesWholeSystem
                        ? $"Attention : Windows ne permet pas d'isoler {appName} sur ce PC, tout le son du PC est enregistré."
                        : $"Seuls le son de {appName} et votre micro (quand il est actif dans {appName}) sont enregistrés."));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Impossible de démarrer l'enregistrement", ex);
            RaiseNotification(new UserNotification("Enregistrement impossible", ex.Message, ToolTipIcon.Error));
            _recorder = null;
            _active = null;
            watch.Detector.StopManually();
            if (_folder is not null)
            {
                _metadata!.Status = CallStatus.Failed;
                _metadata.Error = ex.Message;
                _folder.WriteMetadata(_metadata);
            }
            _folder = null;
        }
    }

    private async Task UpdateRecordingAsync(AppWatch watch, AppSettings settings)
    {
        await _recorder!.AttachProcessesAsync(watch.Roots).ConfigureAwait(false);

        MuteDecision? mute;
        lock (_muteLock) mute = _mute;

        if (mute?.Source == MuteSource.Unknown && !settings.RecordMicWhenMuteUnknown && !_warnedUnknownMute && _recorder.Elapsed > TimeSpan.FromSeconds(15))
        {
            _warnedUnknownMute = true;
            RaiseNotification(new UserNotification(
                "Votre voix n'est pas enregistrée",
                watch.App == CallApp.Teams
                    ? "ListenTeams ne sait pas si votre micro est actif dans Teams. Activez l'API tierce de Teams (menu ListenTeams → Connexion à Teams)."
                    : "ListenTeams ne voit pas les boutons micro de Discord : gardez la fenêtre Discord ouverte (elle peut être en arrière-plan ou réduite).",
                ToolTipIcon.Warning));
        }

        if (watch.App == CallApp.Teams && DateTime.UtcNow - _lastTitleUpdate > TimeSpan.FromSeconds(5))
        {
            _lastTitleUpdate = DateTime.UtcNow;
            _titles.Update(watch.AllProcesses);
        }
    }

    /// <summary>
    /// Keeps the microphone gate in sync with the application. The Teams API pushes changes
    /// instantly (<see cref="ApplyMute"/> runs on each message); the window is read 5 times
    /// per second, so speech right after a mute click is not recorded.
    /// </summary>
    private async Task MuteLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                ApplyMute();
            }
            catch (Exception ex)
            {
                Log.Error("Erreur en lisant l'état du micro", ex);
            }

            try
            {
                await Task.Delay(200, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void ApplyMute()
    {
        var recorder = _recorder;
        var watch = _active;
        if (recorder is null || watch is null) return;

        lock (_muteLock)
        {
            var mute = ResolveMute(watch, _settings.Current);
            if (mute != _mute)
            {
                Log.Info($"Micro {CallApps.Name(watch.App)} : {(mute.Muted ? "coupé" : "actif")} (source : {mute.Source})");
                _mute = mute;
            }
            _muteSourcesUsed.Add(mute.Source);
            recorder.SetMuted(mute.Muted);
        }
    }

    /// <remarks>Call with <see cref="_muteLock"/> held (the window reader is not thread-safe).</remarks>
    private MuteDecision ResolveMute(AppWatch watch, AppSettings settings)
    {
        var api = watch.App == CallApp.Teams ? _api.State : TeamsApiState.Disconnected;
        bool paired = watch.App == CallApp.Teams && _api.HasToken;
        bool apiTrusted = paired && api.Connected && api.HasMeetingState && api.IsInMeeting;
        bool? window = apiTrusted ? null : _windowReader?.ReadMuted(watch.AllProcesses);
        return MuteResolver.Resolve(api, paired, window, settings.RecordMicWhenMuteUnknown);
    }

    private async Task MaybeRequestPairingAsync()
    {
        var api = _api.State;
        if (!api.Connected || !api.CanPair || _api.HasToken || _settings.Current.PairingRequestedOnce) return;

        _settings.Update(s => s.PairingRequestedOnce = true);
        if (await _api.RequestPairingAsync().ConfigureAwait(false))
        {
            RaiseNotification(new UserNotification(
                "Autorisez ListenTeams dans Teams",
                "Teams affiche une demande d'autorisation : acceptez-la pour que ListenTeams sache quand votre micro est coupé."));
        }
    }

    private async Task StopRecordingAsync()
    {
        var recorder = _recorder;
        var folder = _folder;
        var metadata = _metadata;
        var watch = _active;
        _recorder = null;
        _folder = null;
        _active = null;
        if (recorder is null || folder is null || metadata is null) return;

        var duration = recorder.Elapsed;
        await recorder.DisposeAsync().ConfigureAwait(false);

        string? title = null;
        if (watch?.App == CallApp.Teams)
        {
            _titles.Update(watch.AllProcesses);
            title = _titles.Title;
        }

        metadata.EndedAt = DateTime.Now;
        metadata.DurationSeconds = duration.TotalSeconds;
        if (title is not null) metadata.Title = title;
        metadata.MuteSource = DescribeMuteSources();
        metadata.Status = CallStatus.Pending;
        folder.WriteMetadata(metadata);
        Log.Info($"Enregistrement terminé ({duration:hh\\:mm\\:ss}) : {folder.Path}");

        if (duration < MinimumCallLength)
        {
            Log.Info("Appel trop court, enregistrement supprimé.");
            TryDelete(folder.Path);
            return;
        }

        if (title is not null) folder.RenameWithTitle(title);
        _processing.Enqueue(folder);
        RaiseNotification(new UserNotification(
            "Appel terminé",
            $"{metadata.Title} ({Core.Summary.SummaryPrompt.FormatDuration(duration)}) — transcription et synthèse en cours…",
            ToolTipIcon.Info,
            folder.Path));
    }

    private string DescribeMuteSources()
    {
        MuteSource[] sources;
        lock (_muteLock) sources = [.. _muteSourcesUsed];
        var names = sources.Select(s => s switch
        {
            MuteSource.TeamsApi => "API Teams",
            MuteSource.AppWindow => "fenêtre de l'application",
            _ => "inconnu",
        });
        return string.Join(", ", names);
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void PublishStatus()
    {
        var recorder = _recorder;
        var active = _active;
        var enabled = _watches.Where(w => w.Enabled).Select(w => w.App).ToList();
        var running = _watches.Where(w => w.Running).Select(w => w.App).ToList();
        var state = recorder is not null ? TrayState.Recording
            : _processing.Pending > 0 ? TrayState.Processing
            : running.Count > 0 ? TrayState.Ready
            : TrayState.Idle;

        Status = new CoordinatorStatus(
            state,
            enabled,
            running,
            recorder is not null ? active?.App : null,
            active?.Detector.IsManual ?? false,
            recorder?.Elapsed ?? TimeSpan.Zero,
            recorder is not null ? CurrentMute() : null,
            _api.State,
            _api.HasToken,
            _processingText,
            _processing.Pending);
        StatusChanged?.Invoke(Status);
    }

    private MuteDecision? CurrentMute()
    {
        lock (_muteLock) return _mute;
    }

    private void RaiseNotification(UserNotification notification) => Notification?.Invoke(notification);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var task in new[] { _loop, _muteLoop })
        {
            if (task is null) continue;
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        if (_recorder is not null)
        {
            // Quitting during a call: finalize the file; it will be processed at next start.
            await StopRecordingAsync().ConfigureAwait(false);
        }

        await _api.DisposeAsync().ConfigureAwait(false);
        if (_apiLoop is not null)
        {
            try
            {
                await _apiLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
        _cts.Dispose();
    }
}
