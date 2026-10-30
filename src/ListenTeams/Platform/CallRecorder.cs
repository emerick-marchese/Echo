using ListenTeams.Core;
using ListenTeams.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ListenTeams.Platform;

internal sealed record RecorderOptions(int OpusBitrate, string MicrophoneDeviceId, bool EchoCancellation, int MuteLookBackMs);

/// <summary>
/// Records one call:
/// <list type="bullet">
/// <item>Teams audio only, through WASAPI per-process loopback on the Teams process tree
/// (other applications, notifications, music… are not captured);</item>
/// <item>the microphone, silenced whenever the user is muted in Teams.</item>
/// </list>
/// Both are mixed live into an Opus file.
/// </summary>
internal sealed class CallRecorder : IAsyncDisposable
{
    private readonly CallMixer _mixer;
    private readonly OpusRecordingWriter _opus;
    private readonly EnvelopeWriter _envelope;
    private readonly RecorderOptions _options;
    private readonly List<WasapiRecorder> _recorders = [];
    private readonly HashSet<int> _capturedProcesses = [];
    private readonly HashSet<int> _failedProcesses = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _mixThread;
    private readonly long _start100ns;
    private bool _stopped;

    private CallRecorder(string audioPath, string envelopePath, string title, RecorderOptions options, bool initiallyMuted)
    {
        _options = options;
        _start100ns = AudioClock.Now100ns();
        _opus = new OpusRecordingWriter(audioPath, options.OpusBitrate, title);
        _envelope = new EnvelopeWriter(envelopePath);
        _mixer = new CallMixer(_start100ns, new MuteGate(initiallyMuted), [_opus, _envelope]);
        _mixThread = new Thread(MixLoop) { IsBackground = true, Name = "ListenTeams mixer", Priority = ThreadPriority.AboveNormal };
    }

    public TimeSpan Elapsed => TimeSpan.FromTicks(AudioClock.Now100ns() - _start100ns);

    public string? MicrophoneName { get; private set; }

    public bool EchoCancellationActive { get; private set; }

    public bool CapturesWholeSystem { get; private set; }

    public static async Task<CallRecorder> StartAsync(
        string audioPath, string envelopePath, string title, RecorderOptions options,
        IReadOnlyList<int> teamsProcessIds, bool initiallyMuted)
    {
        var recorder = new CallRecorder(audioPath, envelopePath, title, options, initiallyMuted);
        try
        {
            recorder.StartMicrophone();
            await recorder.AttachTeamsProcessesAsync(teamsProcessIds).ConfigureAwait(false);
            recorder._mixThread.Start();
            return recorder;
        }
        catch
        {
            await recorder.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Applies a Teams mute change (placed slightly in the past to cover Teams' reporting delay).</summary>
    public void SetMuted(bool muted)
    {
        long when = AudioClock.Now100ns() - _options.MuteLookBackMs * 10_000L;
        _mixer.MicGate.SetMuted(muted, _mixer.FrameAt(when));
    }

    /// <summary>Starts capturing Teams processes we do not capture yet (e.g. Teams restarted).</summary>
    public async Task AttachTeamsProcessesAsync(IReadOnlyList<int> processIds)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            if (!CapturesWholeSystem && !_stopped) StartSystemLoopbackFallback();
            return;
        }

        foreach (int pid in processIds)
        {
            if (_stopped || _failedProcesses.Contains(pid) || !_capturedProcesses.Add(pid)) continue;
            try
            {
                var recorder = await BuildProcessLoopbackAsync(pid).ConfigureAwait(false);
                Attach(recorder, _mixer.AddTrack($"teams-{pid}", TrackRole.Remote));
                Log.Info($"Capture audio de Teams (processus {pid}) : {recorder.WaveFormat}");
            }
            catch (Exception ex)
            {
                Log.Error($"Capture par processus impossible pour Teams ({pid})", ex);
                _capturedProcesses.Remove(pid);
                _failedProcesses.Add(pid);
                if (!CapturesWholeSystem && _capturedProcesses.Count == 0)
                {
                    StartSystemLoopbackFallback();
                }
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041")]
    private static async Task<WasapiRecorder> BuildProcessLoopbackAsync(int pid)
    {
        // The audio engine converts to the requested format; try the best one first.
        WaveFormat[] formats =
        [
            WaveFormat.CreateIeeeFloatWaveFormat(AudioClock.SampleRate, 2),
            new WaveFormat(AudioClock.SampleRate, 16, 2),
            new WaveFormat(44100, 16, 2),
        ];

        Exception? last = null;
        foreach (var format in formats)
        {
            try
            {
                return await new WasapiRecorderBuilder()
                    .WithProcessLoopback((uint)pid, ProcessLoopbackMode.IncludeTargetProcessTree)
                    .WithFormat(format)
                    .WithBufferLength(100)
                    .BuildAsync()
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw last!;
    }

    /// <summary>
    /// Windows older than 10 version 2004 cannot capture a single process: fall back to the
    /// whole default output (other sounds of the PC may then be recorded too).
    /// </summary>
    private void StartSystemLoopbackFallback()
    {
        try
        {
            var recorder = new WasapiRecorderBuilder()
                .WithLoopbackCapture()
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(AudioClock.SampleRate, 2))
                .WithBufferLength(100)
                .Build();
            Attach(recorder, _mixer.AddTrack("system-loopback", TrackRole.Remote));
            CapturesWholeSystem = true;
            Log.Warn("Capture de la sortie audio complète (capture par processus indisponible).");
        }
        catch (Exception ex)
        {
            Log.Error("Capture de la sortie audio impossible", ex);
        }
    }

    private void StartMicrophone()
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        if (!string.IsNullOrEmpty(_options.MicrophoneDeviceId))
        {
            try
            {
                device = enumerator.GetDevice(_options.MicrophoneDeviceId);
                if (device.State != DeviceState.Active) device = null;
            }
            catch (Exception)
            {
                device = null;
            }
        }
        if (device is null && !enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications, out device))
        {
            Log.Warn("Aucun microphone : seule la voix des autres participants sera enregistrée.");
            return;
        }

        MicrophoneName = device!.FriendlyName;
        var track = _mixer.AddTrack("microphone", TrackRole.Me);

        if (_options.EchoCancellation)
        {
            try
            {
                var builder = new WasapiRecorderBuilder()
                    .WithDevice(device)
                    .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(AudioClock.SampleRate, 1))
                    .WithBufferLength(100)
                    .WithCommunicationsMode();
                if (enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Communications, out var speakers) && speakers is not null)
                {
                    builder = builder.WithEchoCancellationReferenceEndpoint(speakers);
                }
                var recorder = builder.Build();
                Attach(recorder, track);
                EchoCancellationActive = true;
                Log.Info($"Microphone « {MicrophoneName} » (annulation d'écho Windows) : {recorder.WaveFormat}");
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Annulation d'écho indisponible sur « {MicrophoneName} » : {ex.Message}");
            }
        }

        var plain = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(AudioClock.SampleRate, 1))
            .WithBufferLength(100)
            .Build();
        Attach(plain, track);
        Log.Info($"Microphone « {MicrophoneName} » : {plain.WaveFormat}");
    }

    private void Attach(WasapiRecorder recorder, TimelineTrack track)
    {
        var converter = new SampleConverter(ToCaptureFormat(recorder.WaveFormat));
        recorder.DataAvailable += (buffer, flags, _, qpcPosition) =>
        {
            var mono = converter.ToMono48k(buffer, flags.HasFlag(AudioClientBufferFlags.Silent));
            long start = qpcPosition > 0
                ? _mixer.FrameAt(qpcPosition)
                : _mixer.FrameAt(AudioClock.Now100ns()) - mono.Length;
            track.Write(mono, start);
        };
        recorder.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null) Log.Error($"Capture « {track.Name} » interrompue", e.Exception);
        };
        try
        {
            recorder.StartRecording();
        }
        catch
        {
            recorder.Dispose();
            throw;
        }
        lock (_recorders) _recorders.Add(recorder);
    }

    private static CaptureFormat ToCaptureFormat(WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32 && format is WaveFormatExtensible ext && ext.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        var encoding = isFloat ? SampleEncoding.Float32 : format.BitsPerSample switch
        {
            16 => SampleEncoding.Pcm16,
            24 => SampleEncoding.Pcm24,
            32 => SampleEncoding.Pcm32,
            _ => throw new NotSupportedException($"Format audio non géré : {format}"),
        };
        return new CaptureFormat(format.SampleRate, format.Channels, encoding);
    }

    private void MixLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                _mixer.Process(AudioClock.Now100ns());
                _stop.Token.WaitHandle.WaitOne(50);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Erreur pendant le mixage audio", ex);
        }
    }

    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;

        WasapiRecorder[] recorders;
        lock (_recorders) recorders = [.. _recorders];
        foreach (var recorder in recorders)
        {
            try
            {
                recorder.StopRecording();
            }
            catch (Exception ex)
            {
                Log.Warn($"Arrêt de capture : {ex.Message}");
            }
        }

        // Give WASAPI a moment to deliver the last packets, then mix everything left.
        await Task.Delay(200).ConfigureAwait(false);
        _stop.Cancel();
        if (_mixThread.IsAlive) _mixThread.Join(TimeSpan.FromSeconds(5));

        try
        {
            _mixer.Flush();
        }
        catch (Exception ex)
        {
            Log.Error("Erreur en finalisant le mixage", ex);
        }

        foreach (var recorder in recorders)
        {
            await recorder.DisposeAsync().ConfigureAwait(false);
        }
        _opus.Finish();
        _envelope.Close();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
