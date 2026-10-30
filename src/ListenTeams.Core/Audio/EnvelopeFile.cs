namespace ListenTeams.Core.Audio;

/// <summary>Loudness of each side of the call over one envelope frame.</summary>
public readonly record struct EnvelopeFrame(float Me, float Remote)
{
    public float Max => Math.Max(Me, Remote);
}

/// <summary>
/// Records the RMS level of the microphone and of the other participants every 50 ms
/// (a few hundred KB per hour). After the call it tells us who was speaking when, and
/// where the silences are, without keeping separate audio tracks on disk.
/// </summary>
public sealed class EnvelopeWriter : IMixerSink, IDisposable
{
    public const int FrameSamples = AudioClock.SampleRate / 20; // 50 ms
    private const uint Magic = 0x564E454C; // "LENV"

    private readonly BinaryWriter _writer;
    private double _meSum;
    private double _remoteSum;
    private int _filled;
    private bool _closed;

    public EnvelopeWriter(string path)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 14);
        _writer = new BinaryWriter(stream);
        _writer.Write(Magic);
        _writer.Write(FrameSamples);
        _writer.Write(AudioClock.SampleRate);
    }

    public void OnMixed(ReadOnlySpan<float> mixed, ReadOnlySpan<float> me, ReadOnlySpan<float> remote)
    {
        for (int i = 0; i < me.Length; i++)
        {
            _meSum += me[i] * me[i];
            _remoteSum += remote[i] * remote[i];
            if (++_filled == FrameSamples) WriteFrame();
        }
    }

    private void WriteFrame()
    {
        if (_filled == 0) return;
        _writer.Write((float)Math.Sqrt(_meSum / _filled));
        _writer.Write((float)Math.Sqrt(_remoteSum / _filled));
        _meSum = _remoteSum = 0;
        _filled = 0;
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        WriteFrame();
        _writer.Dispose();
    }

    public void Dispose() => Close();

    public static Envelope Read(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Fichier d'enveloppe invalide : " + path);
        int frameSamples = reader.ReadInt32();
        int sampleRate = reader.ReadInt32();
        var frames = new List<EnvelopeFrame>();
        while (reader.BaseStream.Length - reader.BaseStream.Position >= 8)
        {
            frames.Add(new EnvelopeFrame(reader.ReadSingle(), reader.ReadSingle()));
        }
        return new Envelope(frames, frameSamples / (double)sampleRate);
    }
}

/// <summary>In-memory envelope of a call.</summary>
public sealed class Envelope
{
    public Envelope(IReadOnlyList<EnvelopeFrame> frames, double frameSeconds)
    {
        Frames = frames;
        FrameSeconds = frameSeconds;
    }

    public IReadOnlyList<EnvelopeFrame> Frames { get; }

    public double FrameSeconds { get; }

    public TimeSpan Duration => TimeSpan.FromSeconds(Frames.Count * FrameSeconds);

    public int FrameIndex(TimeSpan time) =>
        Math.Clamp((int)(time.TotalSeconds / FrameSeconds), 0, Math.Max(0, Frames.Count - 1));

    /// <summary>Total energy of each side between two instants.</summary>
    public (double Me, double Remote) EnergyBetween(TimeSpan start, TimeSpan end)
    {
        if (Frames.Count == 0) return (0, 0);
        int a = FrameIndex(start);
        int b = Math.Max(a, FrameIndex(end));
        double me = 0, remote = 0;
        for (int i = a; i <= b; i++)
        {
            me += Frames[i].Me * Frames[i].Me;
            remote += Frames[i].Remote * Frames[i].Remote;
        }
        return (me, remote);
    }
}
