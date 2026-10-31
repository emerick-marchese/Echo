namespace Echo.Core.Audio;

public enum TrackRole
{
    /// <summary>What Teams plays: the other participants.</summary>
    Remote,

    /// <summary>The local microphone, gated by the Teams mute state.</summary>
    Me,
}

/// <summary>
/// A mono 48 kHz audio track positioned on the recording timeline.
/// Capture callbacks write packets stamped with their capture time; gaps (for example
/// when Teams plays nothing and WASAPI delivers no packets) are filled with silence so
/// that every track stays aligned with the others. Thread-safe.
/// </summary>
public sealed class TimelineTrack
{
    /// <summary>Timestamp jitter we tolerate before inserting silence or dropping samples.</summary>
    private static readonly long ToleranceFrames = AudioClock.SampleRate * 40 / 1000;

    private readonly object _lock = new();
    private float[] _buffer = new float[AudioClock.SampleRate * 4];
    private int _head;
    private int _count;
    private long _readPosition;

    public TimelineTrack(string name, TrackRole role, long startFrame = 0)
    {
        Name = name;
        Role = role;
        _readPosition = startFrame;
    }

    public string Name { get; }

    public TrackRole Role { get; }

    /// <summary>Timeline position (in frames) right after the last buffered sample.</summary>
    public long WritePosition
    {
        get { lock (_lock) return _readPosition + _count; }
    }

    /// <summary>Writes a packet whose first sample was captured at <paramref name="startFrame"/>.</summary>
    public void Write(ReadOnlySpan<float> samples, long startFrame)
    {
        lock (_lock)
        {
            long writePosition = _readPosition + _count;
            long drift = startFrame - writePosition;
            if (drift > ToleranceFrames)
            {
                AppendSilence(drift);
            }
            else if (drift < -ToleranceFrames)
            {
                // Packet overlaps audio we already placed (late packet or a device clock
                // running fast): drop the overlapping part to keep tracks in sync.
                int skip = (int)Math.Min(-drift, samples.Length);
                samples = samples[skip..];
            }

            Append(samples);
        }
    }

    /// <summary>Fills the track with silence up to <paramref name="frame"/> if it has no data there.</summary>
    public void PadTo(long frame)
    {
        lock (_lock)
        {
            long missing = frame - (_readPosition + _count);
            if (missing > 0) AppendSilence(missing);
        }
    }

    /// <summary>Reads exactly <paramref name="destination"/>.Length frames from the read position.</summary>
    public void Read(Span<float> destination)
    {
        lock (_lock)
        {
            int available = Math.Min(destination.Length, _count);
            int first = Math.Min(available, _buffer.Length - _head);
            _buffer.AsSpan(_head, first).CopyTo(destination);
            if (available > first)
            {
                _buffer.AsSpan(0, available - first).CopyTo(destination[first..]);
            }
            destination[available..].Clear();

            _head = (_head + available) % _buffer.Length;
            _count -= available;
            _readPosition += destination.Length;
        }
    }

    private void AppendSilence(long frames)
    {
        // Cap a single gap to avoid absurd allocations if a timestamp is corrupt.
        frames = Math.Min(frames, AudioClock.SampleRate * 600L);
        EnsureCapacity(_count + (int)frames);
        int tail = (_head + _count) % _buffer.Length;
        int remaining = (int)frames;
        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, _buffer.Length - tail);
            Array.Clear(_buffer, tail, chunk);
            tail = (tail + chunk) % _buffer.Length;
            remaining -= chunk;
        }
        _count += (int)frames;
    }

    private void Append(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return;
        EnsureCapacity(_count + samples.Length);
        int tail = (_head + _count) % _buffer.Length;
        int first = Math.Min(samples.Length, _buffer.Length - tail);
        samples[..first].CopyTo(_buffer.AsSpan(tail));
        if (samples.Length > first)
        {
            samples[first..].CopyTo(_buffer.AsSpan(0));
        }
        _count += samples.Length;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length) return;
        int size = _buffer.Length;
        while (size < required) size *= 2;
        var bigger = new float[size];
        int first = Math.Min(_count, _buffer.Length - _head);
        Array.Copy(_buffer, _head, bigger, 0, first);
        Array.Copy(_buffer, 0, bigger, first, _count - first);
        _buffer = bigger;
        _head = 0;
    }
}
