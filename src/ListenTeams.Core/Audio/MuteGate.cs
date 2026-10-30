namespace ListenTeams.Core.Audio;

/// <summary>
/// Silences the microphone track whenever the user is muted in Teams.
/// Mute changes are placed on the recording timeline (with a small look-back that
/// compensates for the delay before Teams reports the change), and applied by the
/// mixer when it reaches that position, with a short fade to avoid clicks.
/// </summary>
public sealed class MuteGate
{
    private const int FadeFrames = AudioClock.SampleRate / 100; // 10 ms

    private readonly object _lock = new();
    private readonly List<(long Frame, bool Muted)> _events = [];
    private bool _currentMuted;
    private float _gain;

    public MuteGate(bool initiallyMuted)
    {
        _currentMuted = initiallyMuted;
        _gain = initiallyMuted ? 0f : 1f;
    }

    /// <summary>Latest known state (including changes not yet reached by the mixer).</summary>
    public bool IsMuted
    {
        get
        {
            lock (_lock) return _events.Count > 0 ? _events[^1].Muted : _currentMuted;
        }
    }

    public void SetMuted(bool muted, long frame)
    {
        lock (_lock)
        {
            bool last = _events.Count > 0 ? _events[^1].Muted : _currentMuted;
            if (last == muted) return;
            long minFrame = _events.Count > 0 ? _events[^1].Frame : 0;
            _events.Add((Math.Max(frame, minFrame), muted));
        }
    }

    /// <summary>Applies the gate to <paramref name="samples"/>, which start at timeline <paramref name="startFrame"/>.</summary>
    public void Apply(Span<float> samples, long startFrame)
    {
        lock (_lock)
        {
            const float step = 1f / FadeFrames;
            int next = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                long frame = startFrame + i;
                while (next < _events.Count && _events[next].Frame <= frame)
                {
                    _currentMuted = _events[next].Muted;
                    next++;
                }

                float target = _currentMuted ? 0f : 1f;
                if (_gain < target) _gain = Math.Min(target, _gain + step);
                else if (_gain > target) _gain = Math.Max(target, _gain - step);
                samples[i] *= _gain;
            }

            if (next > 0) _events.RemoveRange(0, next);
        }
    }
}
