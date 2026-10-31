using System.Collections.Concurrent;
using Echo.Core;
using Echo.Core.Apps;
using Echo.Core.Output;
using Echo.Core.Settings;
using Echo.Core.Teams;
using Echo.Platform;

namespace Echo;

/// <summary>What Echo sees of one calling application.</summary>
internal sealed record AppState(CallApp App, bool Running, bool InCall);

internal sealed record CoordinatorStatus(
    TrayState State,
    IReadOnlyList<AppState> Apps,
    CallApp? RecordingApp,
    DateTime? RecordingStartedUtc,
    bool Manual,
    MuteDecision? Mute,
    TeamsApiState Api,
    bool ApiPaired,
    string? ProcessingText,
    int ProcessingQueueLength)
{
    public bool Recording => RecordingApp is not null;

    public TimeSpan Elapsed => RecordingStartedUtc is { } start ? DateTime.UtcNow - start : TimeSpan.Zero;

    public bool IsRunning(CallApp app) => Apps.Any(a => a.App == app && a.Running);
}

internal sealed record UserNotification(string Title, string Text, ToolTipIcon Icon = ToolTipIcon.Info, string? OpenPath = null);

/// <summary>
/// The recording engine. The user picks Teams or Discord and starts a recording (or lets
/// Echo start it when a call of that application begins); Echo then records the
/// application's sound and the microphone only while it is unmuted in that application,
/// and hands the finished call to the processing queue. One call is recorded at a time.
/// All the logic runs on one background loop (one tick per second).
/// </summary>
internal sealed class CallCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan MinimumCallLength = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CallEndGrace = TimeSpan.FromSeconds(6);

    /// <summary>Detection state of one calling application.</summary>
    private sealed class AppWatch(CallApp app)
    {
        public CallApp App { get; } = app;

        public CallDetector Detector { get; } = new();

        public IReadOnlyList<int> Roots { get; set; } = [];

        public HashSet<int> AllProcesses { get; set; } = [];

        public bool Running => Roots.Count > 0;
    }

    private readonly SettingsStore _settings;
    private readonly IReadOnlyList<AppWatch> _watches = CallApps.All.Select(a => new AppWatch(a.App)).ToList();
    private readonly TeamsLocalApiClient _api;
    private readonly MeetingTitleTracker _titles = new();
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
    private DateTime? _recordingStartedUtc;
    private DateTime _lastTitleUpdate;
    private bool _warnedUnknownMute;
    private bool _callSeen;
    private DateTime? _callGoneSince;
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
            "Echo est connecté à Teams",
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

        Processing = new ProcessingQueue(settings);
        Processing.ProgressChanged += text =>
        {
            _processingText = text;
            PublishStatus();
        };
        Processing.Completed += result => RaiseNotification(new UserNotification(
            "Compte rendu prêt",
            result.SummaryError is null
                ? $"{result.Metadata.Title} — cliquez pour l'ouvrir."
                : $"{result.Metadata.Title} — transcription prête, synthèse indisponible : {result.SummaryError}",
            result.SummaryError is null ? ToolTipIcon.Info : ToolTipIcon.Warning,
            result.Folder.ReportPath));
        Processing.Failed += (folder, ex) => RaiseNotification(new UserNotification(
            "Échec du traitement de l'appel",
            $"{ex.Message} (l'enregistrement audio est conservé).",
            ToolTipIcon.Error,
            folder.Path));
    }

    public event Action<CoordinatorStatus>? StatusChanged;

    public event Action<UserNotification>? Notification;

    public ProcessingQueue Processing { get; }

    public CoordinatorStatus Status { get; private set; } = new(
        TrayState.Idle, [], null, null, false, null, TeamsApiState.Disconnected, false, null, 0);

    /// <summary>Live meter levels of the current recording (0 when not recording).</summary>
    public (float Me, float Others) Levels => _recorder?.Levels ?? (0f, 0f);

    private AppWatch Watch(CallApp app) => _watches.First(w => w.App == app);

    public void Start()
    {
        Processing.Start(_cts.Token);
        foreach (var folder in CallFolder.FindUnfinished(_settings.Current.OutputFolder))
        {
            Log.Info($"Reprise du traitement interrompu : {folder.Path}");
            Processing.Enqueue(folder);
        }

        _apiLoop = Task.Run(() => _api.RunAsync(() => Watch(CallApp.Teams).Running, _cts.Token));
        _loop = Task.Run(LoopAsync);
        _muteLoop = Task.Run(MuteLoopAsync);
    }

    /// <summary>Starts recording <paramref name="app"/> now. Returns false if a recording is already running.</summary>
    public Task<bool> StartRecordingAsync(CallApp app)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Enqueue(async () =>
        {
            if (_active is not null)
            {
                done.TrySetResult(false);
                return;
            }

            var watch = Watch(app);
            var processes = AppProcesses.Snapshot.Take();
            watch.Roots = processes.RootProcessIds(app);
            watch.AllProcesses = processes.AllProcessIds(app);
            watch.Detector.StartManually();
            await StartRecordingAsync(watch).ConfigureAwait(false);
            done.TrySetResult(_active == watch);
        });
        return done.Task;
    }

    public Task StopRecordingAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Enqueue(async () =>
        {
            if (_active is { } watch)
            {
                watch.Detector.StopManually();
                await StopRecordingCoreAsync().ConfigureAwait(false);
            }
            done.TrySetResult();
        });
        return done.Task;
    }

    /// <summary>Asks Teams to show its "allow Echo" prompt (only works during a meeting).</summary>
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
            watch.Roots = processes.RootProcessIds(watch.App);
            watch.AllProcesses = watch.Running ? processes.AllProcessIds(watch.App) : [];

            var detector = watch.Detector;
            detector.SetAppRunning(watch.Running);
            detector.SetApiState(watch.App == CallApp.Teams ? _api.State : TeamsApiState.Disconnected);
            detector.SetMicInUse(watch.Running && MicrophoneUsageMonitor.IsUsingMicrophone(watch.App));

            bool autoStart = settings.AutoRecord && settings.SelectedApp == watch.App;
            switch (detector.Tick(now))
            {
                case CallEvent.Started when autoStart && _active is null:
                    await StartRecordingAsync(watch).ConfigureAwait(false);
                    break;
                case CallEvent.Started:
                    // Not recorded automatically (manual mode, other application, or a
                    // recording already running): forget this call until it ends.
                    detector.StopManually();
                    break;
                case CallEvent.Ended when _active == watch:
                    await StopRecordingCoreAsync().ConfigureAwait(false);
                    break;
            }
        }

        if (_active is { } active && _recorder is not null)
        {
            await UpdateRecordingAsync(active, settings, now).ConfigureAwait(false);
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
            _callSeen = watch.Detector.CallInProgress;
            _callGoneSince = null;
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
            _recordingStartedUtc = DateTime.UtcNow;
            Log.Info($"Enregistrement {appName} démarré ({(watch.Detector.IsManual ? "manuel" : "automatique")}) : {_folder.Path}");

            if (!watch.Running)
            {
                RaiseNotification(new UserNotification(
                    $"{appName} n'est pas lancé",
                    $"L'enregistrement a démarré : le son de {appName} sera capturé dès son ouverture.",
                    ToolTipIcon.Warning));
            }
            else if (_recorder.CapturesWholeSystem)
            {
                RaiseNotification(new UserNotification(
                    $"Enregistrement de l'appel {appName}",
                    $"Attention : Windows ne permet pas d'isoler {appName} sur ce PC, tout le son du PC est enregistré.",
                    ToolTipIcon.Warning));
            }
            else if (settings.NotifyOnCallStart)
            {
                RaiseNotification(new UserNotification(
                    $"Enregistrement de l'appel {appName}",
                    $"Seuls le son de {appName} et votre micro (quand il est actif dans {appName}) sont enregistrés."));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Impossible de démarrer l'enregistrement", ex);
            RaiseNotification(new UserNotification("Enregistrement impossible", ex.Message, ToolTipIcon.Error));
            _recorder = null;
            _active = null;
            _recordingStartedUtc = null;
            watch.Detector.StopManually();
            if (_folder is not null)
            {
                _metadata!.Status = CallStatus.Failed;
                _metadata.Error = ex.Message;
                _folder.WriteMetadata(_metadata);
            }
            _folder = null;
        }
        PublishStatus();
    }

    private async Task UpdateRecordingAsync(AppWatch watch, AppSettings settings, DateTime now)
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
                    ? "Echo ne sait pas si votre micro est actif dans Teams. Activez l'API tierce de Teams (Echo → Paramètres → Teams)."
                    : "Echo ne voit pas les boutons micro de Discord : gardez la fenêtre Discord ouverte (elle peut être en arrière-plan ou réduite).",
                ToolTipIcon.Warning));
        }

        if (watch.App == CallApp.Teams && now - _lastTitleUpdate > TimeSpan.FromSeconds(5))
        {
            _lastTitleUpdate = now;
            _titles.Update(watch.AllProcesses);
        }

        // A recording started by hand ends with the call it captured (if the user wants).
        if (watch.Detector.IsManual && settings.AutoStopAtCallEnd)
        {
            if (watch.Detector.CallInProgress)
            {
                _callSeen = true;
                _callGoneSince = null;
            }
            else if (_callSeen)
            {
                _callGoneSince ??= now;
                if (now - _callGoneSince >= CallEndGrace)
                {
                    Log.Info("Fin de l'appel détectée : arrêt de l'enregistrement.");
                    watch.Detector.StopManually();
                    await StopRecordingCoreAsync().ConfigureAwait(false);
                }
            }
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
                "Autorisez Echo dans Teams",
                "Teams affiche une demande d'autorisation : acceptez-la pour qu'Echo sache quand votre micro est coupé."));
        }
    }

    private async Task StopRecordingCoreAsync()
    {
        var recorder = _recorder;
        var folder = _folder;
        var metadata = _metadata;
        var watch = _active;
        _recorder = null;
        _folder = null;
        _active = null;
        _recordingStartedUtc = null;
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
            Log.Info("Enregistrement trop court, supprimé.");
            TryDelete(folder.Path);
            RaiseNotification(new UserNotification(
                "Enregistrement trop court",
                "Les enregistrements de moins de 8 secondes ne sont pas conservés.",
                ToolTipIcon.Info));
            PublishStatus();
            return;
        }

        if (title is not null) folder.RenameWithTitle(title);
        Processing.Enqueue(folder);
        RaiseNotification(new UserNotification(
            "Enregistrement terminé",
            $"{metadata.Title} ({Core.Summary.SummaryPrompt.FormatDuration(duration)}) — transcription et synthèse en cours…",
            ToolTipIcon.Info,
            folder.Path));
        PublishStatus();
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
        var apps = _watches.Select(w => new AppState(w.App, w.Running, w.Detector.CallInProgress)).ToList();
        var state = recorder is not null ? TrayState.Recording
            : Processing.Pending > 0 ? TrayState.Processing
            : apps.Any(a => a.Running) ? TrayState.Ready
            : TrayState.Idle;

        Status = new CoordinatorStatus(
            state,
            apps,
            recorder is not null ? active?.App : null,
            recorder is not null ? _recordingStartedUtc : null,
            active?.Detector.IsManual ?? false,
            recorder is not null ? CurrentMute() : null,
            _api.State,
            _api.HasToken,
            _processingText,
            Processing.Pending);
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
            await StopRecordingCoreAsync().ConfigureAwait(false);
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
