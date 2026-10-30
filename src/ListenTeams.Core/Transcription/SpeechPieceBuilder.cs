namespace ListenTeams.Core.Transcription;

/// <summary>A run of samples inside a piece, and where it sits in the original call.</summary>
public readonly record struct PieceSpan(int PieceOffset, long OriginalSample, int Length);

/// <summary>Speech-only audio handed to the recogniser in one go.</summary>
public sealed class SpeechPiece
{
    public SpeechPiece(float[] samples, IReadOnlyList<PieceSpan> spans)
    {
        Samples = samples;
        Spans = spans;
    }

    public float[] Samples { get; }

    public IReadOnlyList<PieceSpan> Spans { get; }

    /// <summary>Converts a time inside the piece to the matching time in the call.</summary>
    public TimeSpan ToOriginal(TimeSpan pieceTime)
    {
        if (Spans.Count == 0) return TimeSpan.Zero;
        long p = (long)(pieceTime.TotalSeconds * SpeechPieceBuilder.SampleRate);
        var span = Spans[0];
        foreach (var candidate in Spans)
        {
            if (candidate.PieceOffset > p) break;
            span = candidate;
        }
        long offset = Math.Clamp(p - span.PieceOffset, 0, span.Length);
        return TimeSpan.FromSeconds((span.OriginalSample + offset) / (double)SpeechPieceBuilder.SampleRate);
    }
}

/// <summary>
/// Streams the 16 kHz call audio and keeps only the speech regions, concatenated (with a
/// short pause between them) into pieces of bounded length. Silence never reaches Whisper,
/// and memory stays bounded however long the call is.
/// </summary>
public sealed class SpeechPieceBuilder
{
    public const int SampleRate = 16000;

    private readonly (long Start, long End)[] _regions;
    private readonly int _maxPieceSamples;
    private readonly int _gapSamples;
    private readonly List<float> _piece = [];
    private readonly List<PieceSpan> _spans = [];
    private int _region;
    private bool _inRegion;
    private long _position;

    public SpeechPieceBuilder(IReadOnlyList<TimeRange> regions, TimeSpan? maxPiece = null, TimeSpan? gap = null)
    {
        _regions = regions
            .Select(r => ((long)(r.Start.TotalSeconds * SampleRate), (long)(r.End.TotalSeconds * SampleRate)))
            .Where(r => r.Item2 > r.Item1)
            .ToArray();
        _maxPieceSamples = (int)((maxPiece ?? TimeSpan.FromMinutes(10)).TotalSeconds * SampleRate);
        _gapSamples = (int)((gap ?? TimeSpan.FromSeconds(0.3)).TotalSeconds * SampleRate);
    }

    /// <summary>All speech regions have been collected; the rest of the audio is not needed.</summary>
    public bool Done => _region >= _regions.Length;

    /// <summary>Total speech duration that will be transcribed.</summary>
    public TimeSpan SpeechDuration =>
        TimeSpan.FromSeconds(_regions.Sum(r => r.End - r.Start) / (double)SampleRate);

    /// <summary>Feeds the next samples of the call; returns the pieces that became complete.</summary>
    public List<SpeechPiece> Add(ReadOnlySpan<float> samples)
    {
        var completed = new List<SpeechPiece>();
        int i = 0;
        while (i < samples.Length && _region < _regions.Length)
        {
            var (start, end) = _regions[_region];
            long g = _position + i;
            if (g < start)
            {
                i += (int)Math.Min(start - g, samples.Length - i);
                continue;
            }
            if (g >= end)
            {
                _region++;
                _inRegion = false;
                continue;
            }

            if (!_inRegion)
            {
                if (_piece.Count > 0 && _piece.Count + _gapSamples + (end - g) > _maxPieceSamples)
                {
                    Flush(completed);
                }
                if (_piece.Count > 0)
                {
                    _piece.AddRange(new float[_gapSamples]);
                }
                _inRegion = true;
                _spans.Add(new PieceSpan(_piece.Count, g, 0));
            }

            int n = (int)Math.Min(end - g, samples.Length - i);
            n = Math.Min(n, _maxPieceSamples - _piece.Count);
            if (n <= 0)
            {
                // Very long uninterrupted speech: cut the piece here and carry on.
                Flush(completed);
                _spans.Add(new PieceSpan(0, g, 0));
                continue;
            }

            for (int k = 0; k < n; k++) _piece.Add(samples[i + k]);
            var span = _spans[^1];
            _spans[^1] = span with { Length = span.Length + n };
            i += n;
        }

        _position += samples.Length;
        return completed;
    }

    /// <summary>Returns the last, partial piece (if any).</summary>
    public SpeechPiece? Complete()
    {
        var completed = new List<SpeechPiece>();
        Flush(completed);
        return completed.Count > 0 ? completed[0] : null;
    }

    private void Flush(List<SpeechPiece> completed)
    {
        var spans = _spans.Where(s => s.Length > 0).ToList();
        if (_piece.Count > 0 && spans.Count > 0)
        {
            completed.Add(new SpeechPiece([.. _piece], spans));
        }
        _piece.Clear();
        _spans.Clear();
    }
}
