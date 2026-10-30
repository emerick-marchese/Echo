using System.Diagnostics;

namespace ListenTeams.Core.Audio;

/// <summary>
/// Shared time base for every capture stream. WASAPI timestamps packets with the
/// QueryPerformanceCounter expressed in 100 ns units, so we use the same unit here.
/// </summary>
public static class AudioClock
{
    public const int SampleRate = 48000;

    /// <summary>Current QPC time in 100 ns units (same unit as WASAPI QPC positions).</summary>
    public static long Now100ns()
    {
        long ticks = Stopwatch.GetTimestamp();
        return (long)(ticks * (10_000_000.0 / Stopwatch.Frequency));
    }

    public static long ToFrames(long duration100ns, int sampleRate = SampleRate) =>
        (long)Math.Round(duration100ns * (sampleRate / 10_000_000.0));

    public static TimeSpan FramesToTime(long frames, int sampleRate = SampleRate) =>
        TimeSpan.FromSeconds(frames / (double)sampleRate);
}
