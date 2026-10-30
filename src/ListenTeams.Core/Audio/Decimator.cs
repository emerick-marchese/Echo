namespace ListenTeams.Core.Audio;

/// <summary>
/// Streaming 48 kHz → 16 kHz converter (the rate Whisper expects): low-pass FIR filter
/// followed by keeping one sample out of three.
/// </summary>
public sealed class Decimator
{
    private const int Factor = 3;
    private const int Taps = 95;
    private static readonly float[] Kernel = BuildKernel();

    private readonly float[] _history = new float[Taps - 1];
    private int _phase;

    public static int OutputRate => AudioClock.SampleRate / Factor;

    public void Process(ReadOnlySpan<float> input, List<float> output)
    {
        // Work on history + input so the filter is continuous across blocks.
        var work = new float[_history.Length + input.Length];
        _history.CopyTo(work, 0);
        input.CopyTo(work.AsSpan(_history.Length));

        for (int n = _history.Length + _phase; n < work.Length; n += Factor)
        {
            float acc = 0;
            int start = n - (Taps - 1);
            for (int k = 0; k < Taps; k++) acc += Kernel[k] * work[start + k];
            output.Add(acc);
        }

        _phase = ((_phase - input.Length) % Factor + Factor) % Factor;
        work.AsSpan(work.Length - _history.Length).CopyTo(_history);
    }

    private static float[] BuildKernel()
    {
        // Windowed-sinc low-pass (Blackman), cutoff 7.5 kHz at 48 kHz.
        const double cutoff = 7500.0 / AudioClock.SampleRate;
        var kernel = new float[Taps];
        int m = Taps - 1;
        double sum = 0;
        for (int i = 0; i < Taps; i++)
        {
            double x = i - m / 2.0;
            double sinc = x == 0 ? 2 * Math.PI * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / x;
            double window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / m) + 0.08 * Math.Cos(4 * Math.PI * i / m);
            kernel[i] = (float)(sinc * window);
            sum += kernel[i];
        }
        for (int i = 0; i < Taps; i++) kernel[i] = (float)(kernel[i] / sum);
        return kernel;
    }
}
