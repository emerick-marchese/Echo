namespace Echo.Core.Audio;

/// <summary>Receives the mixed call audio, block by block, in timeline order.</summary>
public interface IMixerSink
{
    /// <param name="mixed">Final mono mix (what goes in the audio file).</param>
    /// <param name="me">Microphone contribution, after the Teams mute gate.</param>
    /// <param name="remote">Contribution of the other participants (what Teams played).</param>
    void OnMixed(ReadOnlySpan<float> mixed, ReadOnlySpan<float> me, ReadOnlySpan<float> remote);
}

/// <summary>
/// Aligns every capture track on a common timeline and mixes them a fixed latency behind
/// real time, which leaves room for late packets and for late mute notifications.
/// </summary>
public sealed class CallMixer
{
    private const int BlockFrames = AudioClock.SampleRate / 10; // 100 ms

    private readonly object _tracksLock = new();
    private readonly List<TimelineTrack> _tracks = [];
    private readonly IReadOnlyList<IMixerSink> _sinks;
    private readonly long _start100ns;
    private readonly long _latencyFrames;
    private readonly float[] _mix = new float[BlockFrames];
    private readonly float[] _me = new float[BlockFrames];
    private readonly float[] _remote = new float[BlockFrames];
    private readonly float[] _scratch = new float[BlockFrames];
    private long _mixedFrames;

    public CallMixer(long start100ns, MuteGate micGate, IReadOnlyList<IMixerSink> sinks, TimeSpan? latency = null)
    {
        _start100ns = start100ns;
        MicGate = micGate;
        _sinks = sinks;
        _latencyFrames = (long)((latency ?? TimeSpan.FromSeconds(1)).TotalSeconds * AudioClock.SampleRate);
    }

    public MuteGate MicGate { get; }

    /// <summary>Number of frames already mixed and handed to the sinks.</summary>
    public long MixedFrames => Interlocked.Read(ref _mixedFrames);

    public TimelineTrack AddTrack(string name, TrackRole role)
    {
        lock (_tracksLock)
        {
            var track = new TimelineTrack(name, role, MixedFrames);
            _tracks.Add(track);
            return track;
        }
    }

    /// <summary>Converts a QPC time (100 ns units) to a timeline frame position.</summary>
    public long FrameAt(long time100ns) => AudioClock.ToFrames(time100ns - _start100ns);

    /// <summary>Mixes everything up to "now minus latency". Call periodically.</summary>
    public void Process(long now100ns) => MixUpTo(FrameAt(now100ns) - _latencyFrames);

    /// <summary>Mixes all remaining buffered audio (end of the recording).</summary>
    public void Flush()
    {
        long end = MixedFrames;
        foreach (var track in SnapshotTracks())
        {
            end = Math.Max(end, track.WritePosition);
        }
        MixUpTo(end);
    }

    private TimelineTrack[] SnapshotTracks()
    {
        lock (_tracksLock) return [.. _tracks];
    }

    private void MixUpTo(long target)
    {
        var tracks = SnapshotTracks();
        while (MixedFrames < target)
        {
            long start = MixedFrames;
            int frames = (int)Math.Min(BlockFrames, target - start);
            var me = _me.AsSpan(0, frames);
            var remote = _remote.AsSpan(0, frames);
            var scratch = _scratch.AsSpan(0, frames);
            me.Clear();
            remote.Clear();

            foreach (var track in tracks)
            {
                track.PadTo(start + frames);
                track.Read(scratch);
                var destination = track.Role == TrackRole.Me ? me : remote;
                for (int i = 0; i < frames; i++) destination[i] += scratch[i];
            }

            MicGate.Apply(me, start);

            var mix = _mix.AsSpan(0, frames);
            for (int i = 0; i < frames; i++)
            {
                mix[i] = SoftClip(me[i] + remote[i]);
            }

            foreach (var sink in _sinks)
            {
                sink.OnMixed(mix, me, remote);
            }

            Interlocked.Add(ref _mixedFrames, frames);
        }
    }

    private static float SoftClip(float x)
    {
        const float knee = 0.9f;
        float a = Math.Abs(x);
        if (a <= knee) return x;
        float y = knee + (1f - knee) * MathF.Tanh((a - knee) / (1f - knee));
        return MathF.CopySign(y, x);
    }
}
