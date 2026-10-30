using ListenTeams.Core.Audio;
using ListenTeams.Core.Output;
using ListenTeams.Core.Summary;
using ListenTeams.Core.Transcription;

namespace ListenTeams.Core.Processing;

/// <param name="SummaryUnavailableReason">Why no summarizer is available (shown in the report), if so.</param>
public sealed record ProcessingOptions(string MyName, string OthersName, string? ModelName, string? SummaryUnavailableReason);

public sealed record ProcessingResult(CallFolder Folder, CallMetadata Metadata, string? SummaryError);

/// <summary>
/// Everything that happens after the call: transcription of the speech parts, speaker
/// labels, summary, and the output files.
/// </summary>
public sealed class CallProcessor
{
    private readonly Func<ITranscriber> _createTranscriber;
    private readonly Func<ISummarizer?> _createSummarizer;

    public CallProcessor(Func<ITranscriber> createTranscriber, Func<ISummarizer?> createSummarizer)
    {
        _createTranscriber = createTranscriber;
        _createSummarizer = createSummarizer;
    }

    public async Task<ProcessingResult> ProcessAsync(CallFolder folder, ProcessingOptions options, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var metadata = folder.ReadMetadata();
        metadata.Status = CallStatus.Processing;
        metadata.Error = null;
        metadata.WhisperModel = options.ModelName;
        folder.WriteMetadata(metadata);

        try
        {
            var envelope = File.Exists(folder.EnvelopePath)
                ? EnvelopeWriter.Read(folder.EnvelopePath)
                : BuildEnvelopeFromAudio(folder.AudioPath);
            if (metadata.DurationSeconds <= 0) metadata.DurationSeconds = envelope.Duration.TotalSeconds;

            progress?.Report("Transcription…");
            var segments = await TranscribeAsync(folder.AudioPath, envelope, progress, cancellationToken).ConfigureAwait(false);
            segments = HallucinationFilter.Filter(segments);
            segments = SpeakerAttributor.Attribute(segments, envelope);
            var paragraphs = TranscriptFormatter.ToParagraphs(segments);
            string transcript = TranscriptFormatter.ToText(paragraphs, options.MyName, options.OthersName);

            await File.WriteAllTextAsync(folder.TranscriptPath, TranscriptHeader(metadata) + transcript, cancellationToken).ConfigureAwait(false);

            string? summary = null;
            string? summaryError = null;
            var summarizer = _createSummarizer();
            if (summarizer is not null && paragraphs.Count > 0)
            {
                progress?.Report("Synthèse…");
                metadata.SummaryProvider = summarizer.DisplayName;
                try
                {
                    var request = new SummaryRequest(metadata.Title, metadata.StartedAt, metadata.Duration, transcript, options.MyName, options.OthersName);
                    summary = await summarizer.SummarizeAsync(request, cancellationToken).ConfigureAwait(false);
                    await File.WriteAllTextAsync(folder.SummaryPath, summary, cancellationToken).ConfigureAwait(false);
                }
                catch (SummaryException ex)
                {
                    summaryError = ex.Message;
                    Log.Warn($"Synthèse impossible pour {folder.Path} : {ex.Message}");
                }
            }
            else if (summarizer is null)
            {
                summaryError = options.SummaryUnavailableReason;
            }

            string report = ReportWriter.Build(metadata, summary, summaryError, paragraphs, options.MyName, options.OthersName);
            await File.WriteAllTextAsync(folder.ReportPath, report, cancellationToken).ConfigureAwait(false);

            metadata.Status = CallStatus.Done;
            metadata.Error = summaryError;
            folder.WriteMetadata(metadata);
            return new ProcessingResult(folder, metadata, summaryError);
        }
        catch (OperationCanceledException)
        {
            metadata.Status = CallStatus.Pending;
            folder.WriteMetadata(metadata);
            throw;
        }
        catch (Exception ex)
        {
            metadata.Status = CallStatus.Failed;
            metadata.Error = ex.Message;
            folder.WriteMetadata(metadata);
            throw;
        }
    }

    private async Task<List<TranscriptSegment>> TranscribeAsync(string audioPath, Envelope envelope, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var regions = SpeechRegionDetector.Detect(envelope);
        var builder = new SpeechPieceBuilder(regions);
        var segments = new List<TranscriptSegment>();
        if (regions.Count == 0) return segments;

        double speechSeconds = Math.Max(1, builder.SpeechDuration.TotalSeconds);
        double transcribedSeconds = 0;

        using var transcriber = _createTranscriber();
        var decimator = new Decimator();
        var block16k = new List<float>(4096);

        async Task TranscribePiece(SpeechPiece piece)
        {
            await foreach (var raw in transcriber.TranscribeAsync(piece.Samples, cancellationToken).ConfigureAwait(false))
            {
                var start = piece.ToOriginal(raw.Start);
                var end = piece.ToOriginal(raw.End);
                if (end < start) end = start;
                segments.Add(new TranscriptSegment(start, end, raw.Text));
            }
            transcribedSeconds += piece.Samples.Length / (double)SpeechPieceBuilder.SampleRate;
            progress?.Report($"Transcription {Math.Min(99, (int)(transcribedSeconds * 100 / speechSeconds))} %");
        }

        foreach (var block in OpusFileDecoder.Decode(audioPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            block16k.Clear();
            decimator.Process(block, block16k);
            foreach (var piece in builder.Add(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(block16k)))
            {
                await TranscribePiece(piece).ConfigureAwait(false);
            }
            if (builder.Done) break;
        }

        if (builder.Complete() is { } last)
        {
            await TranscribePiece(last).ConfigureAwait(false);
        }

        return segments;
    }

    /// <summary>Fallback when the envelope file is missing: loudness of the mix only.</summary>
    private static Envelope BuildEnvelopeFromAudio(string audioPath)
    {
        var frames = new List<EnvelopeFrame>();
        double sum = 0;
        int count = 0;
        foreach (var block in OpusFileDecoder.Decode(audioPath))
        {
            foreach (float s in block)
            {
                sum += s * s;
                if (++count == EnvelopeWriter.FrameSamples)
                {
                    frames.Add(new EnvelopeFrame(0, (float)Math.Sqrt(sum / count)));
                    sum = 0;
                    count = 0;
                }
            }
        }
        return new Envelope(frames, EnvelopeWriter.FrameSamples / (double)AudioClock.SampleRate);
    }

    private static string TranscriptHeader(CallMetadata metadata) =>
        $"{metadata.Title}\n{metadata.StartedAt:dd/MM/yyyy HH:mm} — durée {SummaryPrompt.FormatDuration(metadata.Duration)}\n\n";
}
