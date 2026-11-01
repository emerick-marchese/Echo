using System.Text.Json;
using Echo.Core.Audio;

namespace Echo.Core.Speakers;

/// <summary>Who is talking right now, for the recording screen.</summary>
/// <param name="Channel">Discord voice channel, when known.</param>
/// <param name="ExactNames">Names come from the calling application (Discord), not from voice prints.</param>
public sealed record LiveSpeakers(
    bool MeSpeaking,
    double MeTalkSeconds,
    int? CurrentSpeakerId,
    IReadOnlyList<LiveSpeaker> Speakers,
    string? Channel = null,
    bool ExactNames = false);

/// <param name="Muted">Muted or deafened in the calling application (Discord only).</param>
/// <param name="Present">Still in the call (Discord only; false once they left).</param>
public sealed record LiveSpeaker(int Id, string Name, bool Speaking, double TalkSeconds, bool Muted = false, bool Present = true);

/// <summary>A voice met during a call, saved with the recording to name the transcript.</summary>
public sealed record SpeakerProfile(int Id, string Name, bool Renamed, float[] Centroid, double TalkSeconds);

/// <summary>
/// Follows who speaks during a recording. The other participants' audio is analysed every
/// 0.4 s (voice print of the last 1.6 s); "you" is simply the microphone level after the
/// application's mute gate. Runs on its own thread so capture is never slowed down.
/// </summary>
public sealed class LiveSpeakerMonitor : IDisposable
{
    private const double StepSeconds = 0.4;
    private const int WindowSamples = (int)(1.6 * SpeakerModel.SampleRate);
    private const int StepSamples = (int)(StepSeconds * SpeakerModel.SampleRate);
    private const double SpeechRms = 0.008;   // ≈ -42 dBFS
    private const double MeSpeechRms = 0.012;

    private readonly ISpeakerEmbedder _embedder;
    private readonly OnlineSpeakerTracker _tracker = new();
    private readonly Decimator _decimator = new();
    private readonly List<float> _decimated = new(4096);
    private readonly float[] _ring = new float[WindowSamples];
    private readonly object _lock = new();
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();
    private int _ringFilled;
    private int _ringPosition;
    private double _meSquares;
    private long _meCount;
    private LiveSpeakers _snapshot = new(false, 0, null, []);
    private double _meTalk;

    public LiveSpeakerMonitor(ISpeakerEmbedder embedder)
    {
        _embedder = embedder;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Echo speakers", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Audio played by the call application (mono 48 kHz).</summary>
    public void AddRemote(ReadOnlySpan<float> samples48k)
    {
        lock (_lock)
        {
            _decimated.Clear();
            _decimator.Process(samples48k, _decimated);
            foreach (float s in _decimated)
            {
                _ring[_ringPosition] = s;
                _ringPosition = (_ringPosition + 1) % _ring.Length;
            }
            _ringFilled = Math.Min(_ring.Length, _ringFilled + _decimated.Count);
        }
    }

    /// <summary>Microphone as recorded (silence while muted in the application).</summary>
    public void AddMe(ReadOnlySpan<float> samples, bool muted)
    {
        if (muted) return;
        double sum = 0;
        foreach (float s in samples) sum += s * s;
        lock (_lock)
        {
            _meSquares += sum;
            _meCount += samples.Length;
        }
    }

    public LiveSpeakers Snapshot()
    {
        lock (_lock) return _snapshot;
    }

    public bool Rename(int id, string name)
    {
        lock (_lock)
        {
            bool ok = _tracker.Rename(id, name);
            if (ok) _snapshot = _snapshot with { Speakers = [.. _snapshot.Speakers.Select(s => s.Id == id ? s with { Name = name.Trim() } : s)] };
            return ok;
        }
    }

    public IReadOnlyList<SpeakerProfile> Profiles()
    {
        lock (_lock)
        {
            return _tracker.Speakers.Select(s => new SpeakerProfile(s.Id, s.Name, s.Renamed, s.Centroid, s.TalkSeconds)).ToList();
        }
    }

    private void Loop()
    {
        var window = new float[WindowSamples];
        while (!_stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(StepSeconds)))
        {
            try
            {
                Step(window);
            }
            catch (Exception ex)
            {
                Log.Warn($"Reconnaissance des voix : {ex.Message}");
            }
        }
    }

    private void Step(float[] window)
    {
        bool meSpeaking;
        bool remoteSpeaking;
        lock (_lock)
        {
            double meRms = _meCount > 0 ? Math.Sqrt(_meSquares / _meCount) : 0;
            _meSquares = 0;
            _meCount = 0;
            meSpeaking = meRms >= MeSpeechRms;

            // Last 1.6 s of the other participants, oldest first.
            for (int i = 0; i < WindowSamples; i++) window[i] = _ring[(_ringPosition + i) % _ring.Length];
            remoteSpeaking = _ringFilled >= WindowSamples && Rms(window, WindowSamples - StepSamples) >= SpeechRms;
        }

        if (meSpeaking) _meTalk += StepSeconds;

        TrackedSpeaker? current = null;
        if (remoteSpeaking && _embedder.Embed(window) is { } embedding)
        {
            lock (_lock) current = _tracker.Observe(embedding, StepSeconds);
        }

        lock (_lock)
        {
            int? currentId = remoteSpeaking ? current?.Id : null;
            _snapshot = new LiveSpeakers(
                meSpeaking,
                _meTalk,
                currentId,
                _tracker.Speakers.Select(s => new LiveSpeaker(s.Id, s.Name, s.Id == currentId, s.TalkSeconds)).ToList());
        }
    }

    private static double Rms(float[] samples, int from)
    {
        double sum = 0;
        for (int i = from; i < samples.Length; i++) sum += samples[i] * samples[i];
        return Math.Sqrt(sum / Math.Max(1, samples.Length - from));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void SaveProfiles(string path, IReadOnlyList<SpeakerProfile> profiles) =>
        File.WriteAllText(path, JsonSerializer.Serialize(profiles, Json));

    public static IReadOnlyList<SpeakerProfile> LoadProfiles(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<SpeakerProfile>>(File.ReadAllText(path), Json) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
