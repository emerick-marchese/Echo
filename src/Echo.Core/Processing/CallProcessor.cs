using Echo.Core.Audio;
using Echo.Core.Discord;
using Echo.Core.Output;
using Echo.Core.Settings;
using Echo.Core.Speakers;
using Echo.Core.Summary;
using Echo.Core.Transcription;

namespace Echo.Core.Processing;

/// <param name="SummaryUnavailableReason">Why no summarizer is available (shown in the report), if so.</param>
public sealed record ProcessingOptions(string MyName, string OthersName, string? ModelName, string? SummaryUnavailableReason);

public sealed record ProcessingResult(CallFolder Folder, CallMetadata Metadata, string? SummaryError);

/// <summary>
/// Everything that happens after the call: transcription of the speech parts, who said
/// what (you / each recognised voice), summary, and the output files.
/// </summary>
public sealed class CallProcessor
{
    private static readonly TimeSpan MinVoicePrintSegment = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxVoicePrintSegment = TimeSpan.FromSeconds(8);

    private readonly Func<ITranscriber> _createTranscriber;
    private readonly Func<ISummarizer?> _createSummarizer;
    private readonly Func<ISpeakerEmbedder?>? _createEmbedder;

    public CallProcessor(Func<ITranscriber> createTranscriber, Func<ISummarizer?> createSummarizer, Func<ISpeakerEmbedder?>? createEmbedder = null)
    {
        _createTranscriber = createTranscriber;
        _createSummarizer = createSummarizer;
        _createEmbedder = createEmbedder;
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

            // Connected to Discord during the call: everyone's exact name and speaking turns.
            var discord = DiscordCallData.Load(folder.DiscordPath);
            metadata.MyName = discord?.Me is { Name.Length: > 0 } me && options.MyName == AppSettings.DefaultMyName ? me.Name : null;
            metadata.SpeakerSource = null;
            if (discord is { HasSpeech: true })
            {
                progress?.Report("Pseudos Discord…");
                (segments, metadata.SpeakerNames) = DiscordAttribution.Apply(segments, discord);
                metadata.SpeakerSource = "discord";
            }
            else
            {
                using var embedder = _createEmbedder?.Invoke();
                if (embedder is not null)
                {
                    progress?.Report("Reconnaissance des voix…");
                    var profiles = LiveSpeakerMonitor.LoadProfiles(folder.SpeakersPath);
                    (segments, metadata.SpeakerNames) = IdentifyVoices(folder.AudioPath, segments, embedder, profiles, cancellationToken);
                    metadata.SpeakerSource = "voices";
                }
            }

            var paragraphs = TranscriptFormatter.ToParagraphs(segments);
            var naming = Naming(metadata, options);
            CallOutputs.WriteTranscript(folder, metadata, paragraphs, naming);

            var summary = await SummarizeAsync(folder, metadata, paragraphs, naming, options, progress, cancellationToken).ConfigureAwait(false);
            return Finish(folder, metadata, paragraphs, naming, summary);
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

    /// <summary>Writes the summary again from the existing transcript (e.g. after naming the voices).</summary>
    public async Task<ProcessingResult> ResummarizeAsync(CallFolder folder, ProcessingOptions options, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var metadata = folder.ReadMetadata();
        var paragraphs = File.Exists(folder.TranscriptDataPath)
            ? TranscriptData.Read(folder.TranscriptDataPath)
            : TranscriptData.ParseText(File.ReadAllText(folder.TranscriptPath), options.MyName);
        var naming = Naming(metadata, options);
        CallOutputs.WriteTranscript(folder, metadata, paragraphs, naming);
        var summary = await SummarizeAsync(folder, metadata, paragraphs, naming, options, progress, cancellationToken).ConfigureAwait(false);
        return Finish(folder, metadata, paragraphs, naming, summary);
    }

    private static SpeakerNaming Naming(CallMetadata metadata, ProcessingOptions options) =>
        SpeakerNaming.For(metadata, options.MyName, options.OthersName);

    private sealed record SummaryOutcome(string? Error, string? NewTitle);

    private async Task<SummaryOutcome> SummarizeAsync(
        CallFolder folder, CallMetadata metadata, IReadOnlyList<TranscriptParagraph> paragraphs, SpeakerNaming naming,
        ProcessingOptions options, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var summarizer = _createSummarizer();
        if (summarizer is null) return new SummaryOutcome(options.SummaryUnavailableReason, null);
        if (paragraphs.Count == 0) return new SummaryOutcome(null, null);

        progress?.Report("Synthèse…");
        metadata.SummaryProvider = summarizer.DisplayName;
        try
        {
            string transcript = TranscriptFormatter.ToText(paragraphs, naming);
            var request = new SummaryRequest(metadata.App, metadata.Title, metadata.StartedAt, metadata.Duration, transcript, naming.MyName, naming.OthersName, metadata.HasExactNames);
            string summary = await summarizer.SummarizeAsync(request, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(folder.SummaryPath, summary, cancellationToken).ConfigureAwait(false);
            return new SummaryOutcome(null, metadata.HasDefaultTitle ? TitleFromSummary(summary) : null);
        }
        catch (SummaryException ex)
        {
            Log.Warn($"Synthèse impossible pour {folder.Path} : {ex.Message}");
            return new SummaryOutcome(ex.Message, null);
        }
    }

    private static ProcessingResult Finish(CallFolder folder, CallMetadata metadata, IReadOnlyList<TranscriptParagraph> paragraphs, SpeakerNaming naming, SummaryOutcome summary)
    {
        if (summary.NewTitle is not null) metadata.Title = summary.NewTitle;
        metadata.Status = CallStatus.Done;
        metadata.Error = summary.Error;
        folder.WriteMetadata(metadata);
        CallOutputs.WriteReport(folder, metadata, paragraphs, naming);

        // No title from the app window: name the folder after the summary title.
        if (summary.NewTitle is not null) folder.RenameWithTitle(summary.NewTitle);
        return new ProcessingResult(folder, metadata, summary.Error);
    }

    /// <summary>
    /// Gives each sentence of the other participants a voice: voice prints of the sentences
    /// long enough, grouped by similarity, named after the voices seen during the call.
    /// Short sentences take the voice of the nearest identified sentence.
    /// </summary>
    internal static (List<TranscriptSegment> Segments, Dictionary<int, string> Names) IdentifyVoices(
        string audioPath, List<TranscriptSegment> segments, ISpeakerEmbedder embedder,
        IReadOnlyList<SpeakerProfile> profiles, CancellationToken cancellationToken)
    {
        const int rate = SpeakerModel.SampleRate;
        var wanted = segments
            .Select((s, i) => (Index: i, Segment: s))
            .Where(x => x.Segment.Speaker == Speaker.Others && x.Segment.End - x.Segment.Start >= MinVoicePrintSegment)
            .Select(x =>
            {
                long start = (long)(x.Segment.Start.TotalSeconds * rate);
                long end = Math.Min((long)(x.Segment.End.TotalSeconds * rate), start + (long)(MaxVoicePrintSegment.TotalSeconds * rate));
                return (x.Index, Start: start, End: end);
            })
            .OrderBy(x => x.Start)
            .ToList();
        if (wanted.Count == 0) return (segments, []);

        var prints = new Dictionary<int, float[]>();
        var active = new List<(int Index, long Start, long End, List<float> Audio)>();
        var decimator = new Decimator();
        var block = new List<float>(4096);
        long position = 0;
        int next = 0;
        foreach (var decoded in OpusFileDecoder.Decode(audioPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            block.Clear();
            decimator.Process(decoded, block);
            long blockEnd = position + block.Count;

            while (next < wanted.Count && wanted[next].Start < blockEnd)
            {
                var w = wanted[next++];
                active.Add((w.Index, w.Start, w.End, new List<float>((int)(w.End - w.Start))));
            }
            foreach (var a in active)
            {
                long from = Math.Max(a.Start, position), to = Math.Min(a.End, blockEnd);
                for (long p = from; p < to; p++) a.Audio.Add(block[(int)(p - position)]);
            }
            foreach (var done in active.Where(a => a.End <= blockEnd).ToList())
            {
                if (embedder.Embed([.. done.Audio]) is { } print) prints[done.Index] = print;
                active.Remove(done);
            }

            position = blockEnd;
            if (next >= wanted.Count && active.Count == 0) break;
        }
        foreach (var rest in active)
        {
            if (embedder.Embed([.. rest.Audio]) is { } print) prints[rest.Index] = print;
        }
        if (prints.Count == 0) return (segments, []);

        var indices = prints.Keys.OrderBy(i => i).ToList();
        var embeddings = indices.Select(i => prints[i]).ToList();
        var durations = indices.Select(i => (segments[i].End - segments[i].Start).TotalSeconds).ToList();
        var clusters = SpeakerClustering.Cluster(embeddings, durations: durations);
        var (voices, names) = SpeakerClustering.Name(embeddings, clusters, profiles);
        var voiceOf = indices.Select((segment, k) => (segment, voice: voices[k])).ToDictionary(x => x.segment, x => x.voice);

        var result = new List<TranscriptSegment>(segments.Count);
        for (int i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (segment.Speaker != Speaker.Others)
            {
                result.Add(segment);
                continue;
            }
            int voice = voiceOf.TryGetValue(i, out int v) ? v : NearestVoice(segments, voiceOf, i);
            result.Add(segment with { Voice = voice });
        }

        var used = result.Select(s => s.Voice).Where(v => v > 0).ToHashSet();
        return (result, names.Where(n => used.Contains(n.Key)).ToDictionary());
    }

    private static int NearestVoice(List<TranscriptSegment> segments, Dictionary<int, int> voiceOf, int index)
    {
        var limit = TimeSpan.FromSeconds(20);
        int best = 0;
        var bestDistance = TimeSpan.MaxValue;
        foreach (var (i, voice) in voiceOf)
        {
            var distance = (segments[i].Start - segments[index].Start).Duration();
            if (distance < bestDistance && distance <= limit)
            {
                bestDistance = distance;
                best = voice;
            }
        }
        return best;
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

    /// <summary>First "# Title" line of the summary, if any.</summary>
    internal static string? TitleFromSummary(string summary)
    {
        string? line = summary.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line is null || !line.StartsWith("# ", StringComparison.Ordinal)) return null;
        string title = line[2..].Trim().Trim('*').Trim();
        return title.Length == 0 ? null : title.Length > 80 ? title[..80].Trim() : title;
    }
}
