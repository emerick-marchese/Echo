using System.Runtime.CompilerServices;
using ListenTeams.Core.Audio;
using ListenTeams.Core.Output;
using ListenTeams.Core.Processing;
using ListenTeams.Core.Settings;
using ListenTeams.Core.Summary;
using ListenTeams.Core.Transcription;

namespace ListenTeams.Tests;

public class SpeechRegionTests
{
    [Fact]
    public void Detects_speech_bursts_merges_short_pauses_and_ignores_clicks()
    {
        // 50 ms frames: silence, 2 s speech, 0.5 s pause, 1 s speech, 10 s silence, 1 frame click, silence, 1 s speech.
        var frames = new List<EnvelopeFrame>();
        Add(frames, 20, 0.0005f);
        Add(frames, 40, 0.1f);
        Add(frames, 10, 0.0005f);
        Add(frames, 20, 0.1f);
        Add(frames, 200, 0.0005f);
        Add(frames, 1, 0.2f);
        Add(frames, 100, 0.0005f);
        Add(frames, 20, 0.1f);
        Add(frames, 20, 0.0005f);
        var envelope = new Envelope(frames, 0.05);

        var regions = SpeechRegionDetector.Detect(envelope);

        Assert.Equal(2, regions.Count);
        Assert.Equal(1.0 - 0.35, regions[0].Start.TotalSeconds, 2);
        Assert.Equal(4.5 + 0.35, regions[0].End.TotalSeconds, 2);
        Assert.Equal(19.55 - 0.35, regions[1].Start.TotalSeconds, 2);
        Assert.Equal(20.55 + 0.35, regions[1].End.TotalSeconds, 2);
    }

    [Fact]
    public void Silent_call_has_no_region()
    {
        var frames = Enumerable.Repeat(new EnvelopeFrame(0, 0), 1000).ToList();
        Assert.Empty(SpeechRegionDetector.Detect(new Envelope(frames, 0.05)));
    }

    private static void Add(List<EnvelopeFrame> frames, int count, float remote)
    {
        for (int i = 0; i < count; i++) frames.Add(new EnvelopeFrame(0, remote));
    }
}

public class SpeechPieceBuilderTests
{
    private const int Rate = SpeechPieceBuilder.SampleRate;

    [Fact]
    public void Keeps_only_regions_and_maps_times_back()
    {
        var regions = new List<TimeRange>
        {
            new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)),
            new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11)),
        };
        var builder = new SpeechPieceBuilder(regions, gap: TimeSpan.FromSeconds(0.5));
        var audio = Enumerable.Range(0, Rate * 12).Select(i => (float)i).ToArray();

        var pieces = new List<SpeechPiece>();
        for (int i = 0; i < audio.Length; i += 1000)
        {
            pieces.AddRange(builder.Add(audio.AsSpan(i, Math.Min(1000, audio.Length - i))));
        }
        if (builder.Complete() is { } last) pieces.Add(last);

        var piece = Assert.Single(pieces);
        Assert.Equal(Rate * 2 + Rate / 2, piece.Samples.Length);
        Assert.Equal(Rate * 1, piece.Samples[0]);                 // first sample of region 1
        Assert.Equal(Rate * 10, piece.Samples[Rate + Rate / 2]);  // first sample of region 2, after the gap
        Assert.Equal(1.5, piece.ToOriginal(TimeSpan.FromSeconds(0.5)).TotalSeconds, 3);
        Assert.Equal(10.25, piece.ToOriginal(TimeSpan.FromSeconds(1.75)).TotalSeconds, 3);
        Assert.True(builder.Done);
    }

    [Fact]
    public void Pieces_are_bounded_and_long_speech_is_split()
    {
        var regions = new List<TimeRange>
        {
            new(TimeSpan.Zero, TimeSpan.FromSeconds(25)),
            new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(34)),
        };
        var builder = new SpeechPieceBuilder(regions, maxPiece: TimeSpan.FromSeconds(10), gap: TimeSpan.Zero);
        var pieces = builder.Add(new float[Rate * 40]);
        if (builder.Complete() is { } last) pieces.Add(last);

        // 25 s of speech is cut into 10 + 10 + 5 s; the next 4 s region still fits in the last piece.
        Assert.Equal(3, pieces.Count);
        Assert.All(pieces, p => Assert.True(p.Samples.Length <= Rate * 10));
        Assert.Equal(10, pieces[1].ToOriginal(TimeSpan.Zero).TotalSeconds, 3);
        Assert.Equal(20, pieces[2].ToOriginal(TimeSpan.Zero).TotalSeconds, 3);
        Assert.Equal(30, pieces[2].ToOriginal(TimeSpan.FromSeconds(5)).TotalSeconds, 3);
        Assert.Equal(29, pieces.Sum(p => p.Samples.Length) / (double)Rate, 3);
    }
}

public class TranscriptTests
{
    [Fact]
    public void Hallucinations_annotations_and_loops_are_removed()
    {
        var segments = new[]
        {
            Seg(0, 2, "Bonjour à tous."),
            Seg(2, 3, "[Musique]"),
            Seg(3, 5, "Sous-titres réalisés par la communauté d'Amara.org"),
            Seg(5, 6, "D'accord."),
            Seg(6, 7, "D'accord."),
            Seg(7, 8, "D'accord."),
            Seg(8, 9, "On commence (rires) maintenant."),
        };
        var filtered = HallucinationFilter.Filter(segments).Select(s => s.Text).ToList();
        Assert.Equal(["Bonjour à tous.", "D'accord.", "D'accord.", "On commence maintenant."], filtered);
    }

    [Fact]
    public void Speakers_are_attributed_from_the_envelope()
    {
        var frames = new List<EnvelopeFrame>();
        for (int i = 0; i < 40; i++) frames.Add(new EnvelopeFrame(0.2f, 0.01f));  // 0–2 s: me
        for (int i = 0; i < 40; i++) frames.Add(new EnvelopeFrame(0.0f, 0.3f));   // 2–4 s: others
        var envelope = new Envelope(frames, 0.05);

        var attributed = SpeakerAttributor.Attribute([Seg(0.1, 1.9, "moi"), Seg(2.1, 3.9, "eux")], envelope);

        Assert.Equal(Speaker.Me, attributed[0].Speaker);
        Assert.Equal(Speaker.Others, attributed[1].Speaker);
    }

    [Fact]
    public void Paragraphs_group_consecutive_segments_of_the_same_speaker()
    {
        var segments = new List<TranscriptSegment>
        {
            Seg(0, 2, "Bonjour,", Speaker.Me),
            Seg(2.5, 4, "ça va ?", Speaker.Me),
            Seg(4.2, 6, "Très bien.", Speaker.Others),
            Seg(20, 21, "Autre sujet.", Speaker.Others),
        };
        var paragraphs = TranscriptFormatter.ToParagraphs(segments);
        Assert.Equal(3, paragraphs.Count);
        Assert.Equal("Bonjour, ça va ?", paragraphs[0].Text);

        string text = TranscriptFormatter.ToText(paragraphs, "Moi", "Participants");
        Assert.StartsWith("[00:00:00] Moi : Bonjour, ça va ?", text);
        Assert.Contains("[00:00:20] Participants : Autre sujet.", text);
    }

    private static TranscriptSegment Seg(double start, double end, string text, Speaker speaker = Speaker.Others) =>
        new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text, speaker);
}

public class ReportAndSettingsTests
{
    [Fact]
    public void Report_escapes_text_and_links_timestamps()
    {
        var metadata = new CallMetadata { Title = "Point <hebdo>", StartedAt = new DateTime(2026, 10, 30, 14, 5, 0), DurationSeconds = 600 };
        var paragraphs = new List<TranscriptParagraph>
        {
            new(TimeSpan.FromSeconds(75.5), TimeSpan.FromSeconds(80), Speaker.Me, "<script>alert(1)</script>"),
        };
        string html = ReportWriter.Build(metadata, "# Titre\n\n## Déroulé\n- [00:01:15] Ouverture", null, paragraphs, "Moi", "Participants");

        Assert.Contains("Point &lt;hebdo&gt;", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("data-t=\"75\">[00:01:15]</button>", html);
        Assert.Contains("data-t=\"75.5\"", html);
        Assert.Contains("src=\"appel.opus\"", html);
        Assert.Contains("vendredi 30 octobre 2026", html);
    }

    [Fact]
    public void Settings_round_trip_and_secret_is_protected()
    {
        string path = Path.Combine(TestPaths.TempDir(), "settings.json");
        var store = new SettingsStore(path, new ReversingProtector());
        store.Update(s =>
        {
            s.OpusBitrate = 32000;
            s.SummaryProvider = SummaryProvider.Ollama;
            store.SetClaudeApiKey(s, "sk-ant-secret");
        });

        string json = File.ReadAllText(path);
        Assert.DoesNotContain("sk-ant-secret", json);
        Assert.Contains("\"Ollama\"", json);

        var reloaded = new SettingsStore(path, new ReversingProtector());
        Assert.Equal(32000, reloaded.Current.OpusBitrate);
        Assert.Equal("sk-ant-secret", reloaded.ClaudeApiKey);
    }

    [Fact]
    public void Folder_names_are_sanitized()
    {
        Assert.Equal("Réunion projet  Q4", CallFolder.SanitizeFileName("Réunion: projet / Q4?"));
        Assert.Equal(60, CallFolder.SanitizeFileName(new string('a', 100)).Length);
    }

    private sealed class ReversingProtector : ISecretProtector
    {
        public string Protect(string secret) => new(secret.Reverse().ToArray());

        public string? Unprotect(string protectedSecret) => new(protectedSecret.Reverse().ToArray());
    }
}

/// <summary>End to end: record a synthetic call, then run the whole post-call pipeline.</summary>
public class PipelineTests
{
    [Fact]
    public async Task Synthetic_call_produces_transcript_summary_and_report()
    {
        string root = TestPaths.TempDir();
        var folder = CallFolder.Create(root, new DateTime(2026, 10, 30, 9, 30, 0));
        folder.WriteMetadata(new CallMetadata { Title = "Test", StartedAt = new DateTime(2026, 10, 30, 9, 30, 0), Status = CallStatus.Pending });

        // 12 s call: I speak 1–3 s, others speak 5–8 s, I speak while muted in Teams at 9–11 s.
        using (var opus = new OpusRecordingWriter(folder.AudioPath, 24000, "Test"))
        using (var envelope = new EnvelopeWriter(folder.EnvelopePath))
        {
            var gate = new MuteGate(false);
            var mixer = new CallMixer(0, gate, [opus, envelope], TimeSpan.Zero);
            var remote = mixer.AddTrack("teams", TrackRole.Remote);
            var me = mixer.AddTrack("mic", TrackRole.Me);
            me.Write(CodecTests.Sine(300, 0.3f, 48000 * 2), 48000 * 1);
            remote.Write(CodecTests.Sine(500, 0.3f, 48000 * 3), 48000 * 5);
            gate.SetMuted(true, 48000 * 8 + 24000);
            me.Write(CodecTests.Sine(300, 0.3f, 48000 * 2), 48000 * 9);
            remote.PadTo(48000 * 12);
            mixer.Flush();
            opus.Finish();
            envelope.Close();
        }

        var transcriber = new FakeTranscriber();
        var summarizer = new FakeSummarizer();
        var processor = new CallProcessor(() => transcriber, () => summarizer);
        var result = await processor.ProcessAsync(folder, new ProcessingOptions("Alice", "Équipe", "fake", null), null, CancellationToken.None);

        Assert.Equal(CallStatus.Done, result.Metadata.Status);
        Assert.Null(result.SummaryError);

        // Only the two real speech parts reach the recogniser; the muted part is silent.
        double transcribed = transcriber.Pieces.Sum(p => p.Length) / (double)SpeechPieceBuilder.SampleRate;
        Assert.InRange(transcribed, 5.0, 7.0);

        string transcript = File.ReadAllText(folder.TranscriptPath);
        Assert.Contains("] Alice : phrase 1", transcript);
        Assert.Contains("] Équipe : phrase 2", transcript);
        Assert.DoesNotContain("phrase 3", transcript);

        Assert.Contains("Alice : phrase 1", summarizer.LastRequest!.Transcript);
        Assert.Equal("# Synthèse factice", File.ReadAllText(folder.SummaryPath));
        string html = File.ReadAllText(folder.ReportPath);
        Assert.Contains("Synthèse factice", html);
        Assert.Contains("phrase 2", html);
        Assert.Equal(CallStatus.Done, folder.ReadMetadata().Status);
    }

    [Fact]
    public async Task Summary_failure_still_produces_transcript_and_report()
    {
        string root = TestPaths.TempDir();
        var folder = CallFolder.Create(root, DateTime.Now);
        folder.WriteMetadata(new CallMetadata { Title = "Test", StartedAt = DateTime.Now, Status = CallStatus.Pending });
        using (var opus = new OpusRecordingWriter(folder.AudioPath, 24000, "Test"))
        using (var envelope = new EnvelopeWriter(folder.EnvelopePath))
        {
            var mixer = new CallMixer(0, new MuteGate(false), [opus, envelope], TimeSpan.Zero);
            mixer.AddTrack("teams", TrackRole.Remote).Write(CodecTests.Sine(500, 0.3f, 48000 * 2), 48000);
            mixer.Flush();
        }

        var processor = new CallProcessor(() => new FakeTranscriber(), () => new FakeSummarizer { Fail = true });
        var result = await processor.ProcessAsync(folder, new ProcessingOptions("Moi", "Participants", "fake", null), null, CancellationToken.None);

        Assert.Equal("API indisponible", result.SummaryError);
        Assert.True(File.Exists(folder.TranscriptPath));
        Assert.Contains("Synthèse indisponible : API indisponible", File.ReadAllText(folder.ReportPath));
    }

    /// <summary>Returns one segment per speech region it receives, numbered.</summary>
    private sealed class FakeTranscriber : ITranscriber
    {
        private int _count;

        public List<float[]> Pieces { get; } = [];

        public async IAsyncEnumerable<RawSegment> TranscribeAsync(float[] samples, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            Pieces.Add(samples);
            const int rate = SpeechPieceBuilder.SampleRate;
            int? start = null;
            for (int i = 0; i <= samples.Length; i += rate / 20)
            {
                bool loud = i < samples.Length && Math.Abs(samples.Skip(i).Take(rate / 20).DefaultIfEmpty().Max(Math.Abs)) > 0.05f;
                if (loud && start is null) start = i;
                if (!loud && start is { } s)
                {
                    yield return new RawSegment(TimeSpan.FromSeconds(s / (double)rate), TimeSpan.FromSeconds(i / (double)rate), $"phrase {++_count}", 0.9f);
                    start = null;
                }
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeSummarizer : ISummarizer
    {
        public bool Fail { get; init; }

        public SummaryRequest? LastRequest { get; private set; }

        public string DisplayName => "Fake";

        public Task<string> SummarizeAsync(SummaryRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (Fail) throw new SummaryException("API indisponible");
            return Task.FromResult("# Synthèse factice");
        }
    }
}
