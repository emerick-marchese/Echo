using System.Buffers.Binary;

namespace Echo.Core.Audio;

public enum SampleEncoding
{
    Float32,
    Pcm16,
    Pcm24,
    Pcm32,
}

/// <summary>Describes the raw format delivered by a capture device.</summary>
public readonly record struct CaptureFormat(int SampleRate, int Channels, SampleEncoding Encoding)
{
    public int BytesPerSample => Encoding switch
    {
        SampleEncoding.Pcm16 => 2,
        SampleEncoding.Pcm24 => 3,
        _ => 4,
    };

    public int BlockAlign => BytesPerSample * Channels;
}

/// <summary>Converts interleaved capture buffers into mono float samples at 48 kHz.</summary>
public sealed class SampleConverter
{
    private readonly CaptureFormat _format;
    private readonly LinearResampler? _resampler;
    private float[] _mono = [];

    public SampleConverter(CaptureFormat format)
    {
        if (format.Channels <= 0) throw new ArgumentOutOfRangeException(nameof(format));
        _format = format;
        if (format.SampleRate != AudioClock.SampleRate)
        {
            _resampler = new LinearResampler(format.SampleRate, AudioClock.SampleRate);
        }
    }

    public CaptureFormat Format => _format;

    /// <summary>
    /// Converts <paramref name="data"/> to mono 48 kHz. The returned span is only valid
    /// until the next call.
    /// </summary>
    public ReadOnlySpan<float> ToMono48k(ReadOnlySpan<byte> data, bool silent)
    {
        int frames = data.Length / _format.BlockAlign;
        if (_mono.Length < frames) _mono = new float[Math.Max(frames, _mono.Length * 2)];
        Span<float> mono = _mono.AsSpan(0, frames);

        if (silent)
        {
            mono.Clear();
        }
        else
        {
            int channels = _format.Channels;
            int bps = _format.BytesPerSample;
            float scale = 1f / channels;
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                int offset = f * _format.BlockAlign;
                for (int c = 0; c < channels; c++)
                {
                    sum += ReadSample(data.Slice(offset + c * bps, bps));
                }
                mono[f] = sum * scale;
            }
        }

        return _resampler is null ? mono : _resampler.Process(mono);
    }

    private float ReadSample(ReadOnlySpan<byte> s) => _format.Encoding switch
    {
        SampleEncoding.Float32 => BinaryPrimitives.ReadSingleLittleEndian(s),
        SampleEncoding.Pcm16 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
        SampleEncoding.Pcm24 => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8388608f,
        SampleEncoding.Pcm32 => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
        _ => 0f,
    };
}

/// <summary>
/// Minimal streaming linear-interpolation resampler. Only used as a safety net when a
/// device refuses the 48 kHz format we ask WASAPI for; speech survives it fine.
/// </summary>
public sealed class LinearResampler
{
    private readonly double _step;
    private double _position;
    private float _previous;
    private float[] _output = [];

    public LinearResampler(int inputRate, int outputRate)
    {
        _step = inputRate / (double)outputRate;
    }

    public ReadOnlySpan<float> Process(ReadOnlySpan<float> input)
    {
        int max = (int)Math.Ceiling((input.Length + 1) / _step) + 1;
        if (_output.Length < max) _output = new float[max];

        int n = 0;
        // _position is relative to the start of `input`, where index -1 is `_previous`.
        while (_position < input.Length - 1 + 1e-9)
        {
            int i = (int)Math.Floor(_position);
            double frac = _position - i;
            float a = i < 0 ? _previous : input[i];
            float b = i + 1 < input.Length ? input[i + 1] : input[^1];
            _output[n++] = (float)(a + (b - a) * frac);
            _position += _step;
        }

        if (input.Length > 0)
        {
            _previous = input[^1];
            _position -= input.Length;
        }

        return _output.AsSpan(0, n);
    }
}
