using System.Collections.Concurrent;
using ListenTeams.Core;
using ListenTeams.Core.Output;
using ListenTeams.Core.Settings;
using ListenTeams.Core.Teams;
using ListenTeams.Platform;

namespace ListenTeams;

internal sealed record CoordinatorStatus(
    TrayState State,
    bool TeamsRunning,
    bool Recording,
    bool Manual,
    TimeSpan Elapsed,
    MuteDecision? Mute,
    TeamsApiState Api,
    bool ApiPaired,
    string? ProcessingText,
    int ProcessingQueueLength);

internal sealed record UserNotification(string Title, string Text, ToolTipIcon Icon = ToolTipIcon.Info, string? OpenPath = null);

/// <summary>
/// Watches Teams, starts and stops recordings, keeps the microphone gate in sync with the
/// Teams mute state, and hands finished calls to the processing queue.
/// All the logic runs on one background loop (one tick per second).
/// </summary>
internal sealed class CallCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan MinimumCallLength = TimeSpan.FromSeconds(8);

    private readonly SettingsStore _settings;
    private readonly CallDetector _detector = new();
    private readonly TeamsLocalApiClient _api;
    private readonly TeamsWindowMuteReader _windowReader = new();
    private readonly MeetingTitleTracker _titles = new();
    private readonly ProcessingQueue _processing;
    private readonly ConcurrentQueue<Func<Task>> _commands = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<MuteSource> _muteSourcesUsed = [];
    private readonly object _muteLock = new();

    private volatile CallRecorder? _recorder;
    private volatile HashSet<int> _teamsProcesses = [];
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
        TrayState.Idle, false, false, false, TimeSpan.Zero, null, TeamsApiState.Disconnected, false, null, 0);

    public void Start()
    {
        _processing.Start(_cts.Token);
        foreach (var folder in CallFolder.FindUnfinished(_settings.Current.OutputFolder))
        {
            Log.Info($"Reprise du traitement interrompu : {folder.Path}");
            _processing.Enqueue(folder);
        }

        _apiLoop = Task.Run(() => _api.RunAsync(() => _detector.TeamsRunning, _cts.Token));
        _loop = Task.Run(LoopAsync);
        _muteLoop = Task.Run(MuteLoopAsync);
    }

    public void StartManually() => _commands.Enqueue(async () =>
    {
        if (_detector.StartManually() == CallEvent.Started)
        {
            await StartRecordingAsync(TeamsProcesses.FindRootProcessIds()).ConfigureAwait(false);
        }
    });

    public void StopManually() => _commands.Enqueue(async () =>
    {
        if (_detector.StopManually() == CallEvent.Ended)
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
                Log.Error("Erreur dans la surveillance de Teams", ex);
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

        var roots = TeamsProcesses.FindRootProcessIds();
        _teamsProcesses = roots.Count > 0 ? TeamsProcesses.FindAllProcessIds() : [];
        _detector.SetTeamsRunning(roots.Count > 0);
        _detector.SetApiState(_api.State);
        _detector.SetMicInUse(roots.Count > 0 && MicrophoneUsageMonitor.IsTeamsUsingMicrophone());

        switch (_detector.Tick(DateTime.UtcNow))
        {
            case CallEvent.Started:
                await StartRecordingAsync(roots).ConfigureAwait(false);
                break;
            case CallEvent.Ended:
                await StopRecordingAsync().ConfigureAwait(false);
                break;
        }

        if (_recorder is not null)
        {
            await UpdateRecordingAsync(roots).ConfigureAwait(false);
            await MaybeRequestPairingAsync().ConfigureAwait(false);
        }

        PublishStatus();
    }

    private async Task StartRecordingAsync(IReadOnlyList<int> teamsRoots)
    {
        var settings = _settings.Current;
        var startedAt = DateTime.Now;
        try
        {
            Directory.CreateDirectory(settings.OutputFolder);
            _folder = CallFolder.Create(settings.OutputFolder, startedAt);
            _metadata = new CallMetadata { StartedAt = startedAt, Status = CallStatus.Recording };
            _folder.WriteMetadata(_metadata);

            var allTeams = TeamsProcesses.FindAllProcessIds();
            _teamsProcesses = allTeams;
            _titles.OnCallStarted(allTeams);
            _lastTitleUpdate = DateTime.MinValue;
            _warnedUnknownMute = false;
            MuteDecision initialMute;
            lock (_muteLock)
            {
                _muteSourcesUsed.Clear();
                _windowReader.Reset();
                initialMute = ResolveMute(allTeams, settings);
                _mute = initialMute;
                _muteSourcesUsed.Add(initialMute.Source);
            }

            var options = new RecorderOptions(settings.OpusBitrate, settings.MicrophoneDeviceId, settings.EchoCancellation, settings.MuteLookBackMs);
            _recorder = await CallRecorder.StartAsync(_folder.AudioPath, _folder.EnvelopePath, "Appel Teams", options, teamsRoots, initialMute.Muted).ConfigureAwait(false);
            Log.Info($"Enregistrement démarré ({(_detector.IsManual ? "manuel" : "automatique")}) : {_folder.Path}");

            if (settings.NotifyOnCallStart)
            {
                RaiseNotification(new UserNotification(
                    "Enregistrement de l'appel Teams",
                    _recorder.CapturesWholeSystem
                        ? "Attention : Windows ne permet pas d'isoler Teams sur ce PC, tout le son du PC est enregistré."
                        : "Seuls le son de Teams et votre micro (quand il est actif dans Teams) sont enregistrés."));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Impossible de démarrer l'enregistrement", ex);
            RaiseNotification(new UserNotification("Enregistrement impossible", ex.Message, ToolTipIcon.Error));
            _recorder = null;
            _detector.StopManually();
            if (_folder is not null)
            {
                _metadata!.Status = CallStatus.Failed;
                _metadata.Error = ex.Message;
                _folder.WriteMetadata(_metadata);
            }
            _folder = null;
        }
    }

    private async Task UpdateRecordingAsync(IReadOnlyList<int> teamsRoots)
    {
        var settings = _settings.Current;
        await _recorder!.AttachTeamsProcessesAsync(teamsRoots).ConfigureAwait(false);

        var allTeams = _teamsProcesses;
        MuteDecision? mute;
        lock (_muteLock) mute = _mute;

        if (mute?.Source == MuteSource.Unknown && !settings.RecordMicWhenMuteUnknown && !_warnedUnknownMute && _recorder.Elapsed > TimeSpan.FromSeconds(15))
        {
            _warnedUnknownMute = true;
            RaiseNotification(new UserNotification(
                "Votre voix n'est pas enregistrée",
                "ListenTeams ne sait pas si votre micro est actif dans Teams. Activez l'API tierce de Teams (menu ListenTeams → Connexion à Teams).",
                ToolTipIcon.Warning));
        }

        if (DateTime.UtcNow - _lastTitleUpdate > TimeSpan.FromSeconds(5))
        {
            _lastTitleUpdate = DateTime.UtcNow;
            _titles.Update(allTeams);
        }
    }

    /// <summary>
    /// Keeps the microphone gate in sync with Teams. The API pushes changes instantly
    /// (<see cref="ApplyMute"/> runs on each message); the window fallback is polled 5 times
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
                Log.Error("Erreur en lisant l'état du micro Teams", ex);
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
        if (recorder is null) return;

        lock (_muteLock)
        {
            var mute = ResolveMute(_teamsProcesses, _settings.Current);
            if (mute != _mute)
            {
                Log.Info($"Micro Teams : {(mute.Muted ? "coupé" : "actif")} (source : {mute.Source})");
                _mute = mute;
            }
            _muteSourcesUsed.Add(mute.Source);
            recorder.SetMuted(mute.Muted);
        }
    }

    /// <remarks>Call with <see cref="_muteLock"/> held (the window reader is not thread-safe).</remarks>
    private MuteDecision ResolveMute(HashSet<int> allTeams, AppSettings settings)
    {
        var api = _api.State;
        bool paired = _api.HasToken;
        bool apiTrusted = paired && api.Connected && api.HasMeetingState && api.IsInMeeting;
        bool? window = apiTrusted ? null : _windowReader.ReadMuted(allTeams);
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
        _recorder = null;
        _folder = null;
        if (recorder is null || folder is null || metadata is null) return;

        var duration = recorder.Elapsed;
        await recorder.DisposeAsync().ConfigureAwait(false);
        _titles.Update(TeamsProcesses.FindAllProcessIds());

        metadata.EndedAt = DateTime.Now;
        metadata.DurationSeconds = duration.TotalSeconds;
        metadata.Title = _titles.Title ?? "Appel Teams";
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

        if (_titles.Title is { } title) folder.RenameWithTitle(title);
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
            MuteSource.TeamsWindow => "fenêtre Teams",
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
        var state = recorder is not null ? TrayState.Recording
            : _processing.Pending > 0 ? TrayState.Processing
            : _detector.TeamsRunning ? TrayState.Ready
            : TrayState.Idle;

        Status = new CoordinatorStatus(
            state,
            _detector.TeamsRunning,
            recorder is not null,
            _detector.IsManual,
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
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        if (_muteLoop is not null)
        {
            try
            {
                await _muteLoop.ConfigureAwait(false);
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
