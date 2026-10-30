using System.Runtime.CompilerServices;
using Whisper.net;

namespace ListenTeams.Core.Transcription;

public interface ITranscriber : IDisposable
{
    /// <summary>Transcribes mono 16 kHz audio; segment times are relative to <paramref name="samples"/>.</summary>
    IAsyncEnumerable<RawSegment> TranscribeAsync(float[] samples, CancellationToken cancellationToken);
}

/// <summary>Local speech recognition with whisper.cpp (Whisper.net). Nothing leaves the PC.</summary>
public sealed class WhisperTranscriber : ITranscriber
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;

    public WhisperTranscriber(string modelPath, string language, string? vocabulary, bool useGpu)
    {
        _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = useGpu });

        int threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        var builder = _factory.CreateBuilder()
            .WithThreads(threads)
            .WithProbabilities();

        builder = string.IsNullOrWhiteSpace(language) || language == "auto"
            ? builder.WithLanguageDetection()
            : builder.WithLanguage(language);

        if (!string.IsNullOrWhiteSpace(vocabulary))
        {
            builder = builder.WithPrompt(vocabulary.Trim());
        }

        _processor = builder.Build();
    }

    public async IAsyncEnumerable<RawSegment> TranscribeAsync(float[] samples, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var segment in _processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
        {
            yield return new RawSegment(segment.Start, segment.End, segment.Text, segment.Probability);
        }
    }

    public void Dispose()
    {
        _processor.Dispose();
        _factory.Dispose();
    }
}
