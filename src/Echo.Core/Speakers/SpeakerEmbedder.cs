using SherpaOnnx;

namespace Echo.Core.Speakers;

/// <summary>Turns a few seconds of speech into a "voice print" vector.</summary>
public interface ISpeakerEmbedder : IDisposable
{
    /// <summary>Embedding of mono 16 kHz speech, or null when the audio is too short.</summary>
    float[]? Embed(float[] samples16k);
}

/// <summary>Voice prints with the CAM++ model (3D-Speaker) through sherpa-onnx, on the CPU.</summary>
public sealed class SherpaSpeakerEmbedder : ISpeakerEmbedder
{
    private readonly SpeakerEmbeddingExtractor _extractor;
    private readonly object _lock = new();

    public SherpaSpeakerEmbedder(string modelPath, int threads = 1)
    {
        var config = new SpeakerEmbeddingExtractorConfig
        {
            Model = modelPath,
            NumThreads = threads,
            Provider = "cpu",
        };
        _extractor = new SpeakerEmbeddingExtractor(config);
    }

    public float[]? Embed(float[] samples16k)
    {
        lock (_lock)
        {
            using var stream = _extractor.CreateStream();
            stream.AcceptWaveform(SpeakerModel.SampleRate, samples16k);
            stream.InputFinished();
            return _extractor.IsReady(stream) ? SpeakerMath.Normalize(_extractor.Compute(stream)) : null;
        }
    }

    public void Dispose()
    {
        lock (_lock) _extractor.Dispose();
    }
}

/// <summary>The voice-print model Echo uses and where to find it.</summary>
public static class SpeakerModel
{
    public const int SampleRate = 16000;
    public const string FileName = "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx";
    public const int SizeMb = 28;

    public static string Url => $"https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/{FileName}";

    public static string? Resolve(IEnumerable<string> folders) =>
        folders.Select(f => Path.Combine(f, FileName)).FirstOrDefault(p => File.Exists(p) && new FileInfo(p).Length > 1_000_000);

    public static async Task<string> DownloadAsync(string folder, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, FileName);
        string partial = target + ".part";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Echo/2.0");
        await using (var source = await http.GetStreamAsync(Url, cancellationToken).ConfigureAwait(false))
        await using (var destination = File.Create(partial))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        File.Move(partial, target, overwrite: true);
        return target;
    }
}

public static class SpeakerMath
{
    public static float[] Normalize(float[] v)
    {
        double norm = Math.Sqrt(v.Sum(x => (double)x * x));
        if (norm < 1e-9) return v;
        return v.Select(x => (float)(x / norm)).ToArray();
    }

    /// <summary>Cosine similarity of two vectors (1 = same direction).</summary>
    public static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na < 1e-12 || nb < 1e-12 ? 0 : dot / Math.Sqrt(na * nb);
    }

    /// <summary>Weighted running mean: <paramref name="centroid"/> moved towards <paramref name="v"/>.</summary>
    public static float[] Blend(float[] centroid, float[] v, double weightOfV)
    {
        var result = new float[centroid.Length];
        for (int i = 0; i < result.Length; i++) result[i] = (float)(centroid[i] * (1 - weightOfV) + v[i] * weightOfV);
        return Normalize(result);
    }
}
