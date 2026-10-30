using ListenTeams.Core.Audio;

namespace ListenTeams.Core.Transcription;

public readonly record struct TimeRange(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// Finds the parts of the call where someone speaks, from the loudness envelope.
/// Only those parts are sent to Whisper: it is faster, and Whisper is known to invent
/// sentences ("Sous-titres réalisés par…") on long silences.
/// </summary>
public static class SpeechRegionDetector
{
    public static List<TimeRange> Detect(
        Envelope envelope,
        double minThresholdDb = -52,
        double maxThresholdDb = -38,
        TimeSpan? mergeGap = null,
        TimeSpan? padding = null,
        TimeSpan? minActivity = null)
    {
        var gap = mergeGap ?? TimeSpan.FromSeconds(1.2);
        var pad = padding ?? TimeSpan.FromSeconds(0.35);
        var minActive = minActivity ?? TimeSpan.FromSeconds(0.2);
        var frames = envelope.Frames;
        var regions = new List<TimeRange>();
        if (frames.Count == 0) return regions;

        double threshold = Threshold(frames, minThresholdDb, maxThresholdDb);

        // 1. Raw active runs.
        var runs = new List<(int Start, int End)>();
        int? runStart = null;
        for (int i = 0; i <= frames.Count; i++)
        {
            bool active = i < frames.Count && ToDb(frames[i].Max) >= threshold;
            if (active && runStart is null) runStart = i;
            else if (!active && runStart is { } s)
            {
                runs.Add((s, i));
                runStart = null;
            }
        }

        // 2. Merge runs separated by short pauses, drop isolated clicks.
        int gapFrames = (int)Math.Round(gap.TotalSeconds / envelope.FrameSeconds);
        int minFrames = Math.Max(1, (int)Math.Round(minActive.TotalSeconds / envelope.FrameSeconds));
        var merged = new List<(int Start, int End)>();
        foreach (var run in runs)
        {
            if (merged.Count > 0 && run.Start - merged[^1].End <= gapFrames)
            {
                merged[^1] = (merged[^1].Start, run.End);
            }
            else
            {
                merged.Add(run);
            }
        }

        // 3. Pad and convert to time, merging regions the padding made overlap.
        var total = envelope.Duration;
        foreach (var (start, end) in merged)
        {
            if (end - start < minFrames) continue;
            var a = TimeSpan.FromSeconds(Math.Max(0, start * envelope.FrameSeconds - pad.TotalSeconds));
            var b = TimeSpan.FromSeconds(Math.Min(total.TotalSeconds, end * envelope.FrameSeconds + pad.TotalSeconds));
            if (regions.Count > 0 && a <= regions[^1].End)
            {
                regions[^1] = regions[^1] with { End = b };
            }
            else
            {
                regions.Add(new TimeRange(a, b));
            }
        }

        return regions;
    }

    /// <summary>Adaptive threshold: a bit above the background noise, within sane bounds.</summary>
    internal static double Threshold(IReadOnlyList<EnvelopeFrame> frames, double minDb, double maxDb)
    {
        var levels = frames.Select(f => ToDb(f.Max)).Where(db => db > -90).OrderBy(db => db).ToArray();
        if (levels.Length == 0) return minDb;
        double noiseFloor = levels[(int)(levels.Length * 0.15)];
        return Math.Clamp(noiseFloor + 12, minDb, maxDb);
    }

    internal static double ToDb(float rms) => rms <= 1e-9f ? -200 : 20 * Math.Log10(rms);
}
