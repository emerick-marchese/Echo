using Echo.Core.Audio;
using Echo.Core.Output;
using Echo.Core.Processing;
using Echo.Core.Speakers;
using Echo.Core.Transcription;

namespace Echo.Tests;

/// <summary>Voice prints that depend only on the pitch of a test tone: two tones = two "voices".</summary>
internal sealed class ToneEmbedder : ISpeakerEmbedder
{
    public int Calls { get; private set; }

    public float[]? Embed(float[] samples)
    {
        Calls++;
        if (samples.Length < 4000) return null;
        // Zero crossings give the tone frequency; each frequency band maps to one direction.
        int crossings = 0;
        for (int i = 1; i < samples.Length; i++) if ((samples[i - 1] < 0) != (samples[i] < 0)) crossings++;
        double frequency = crossings / 2.0 * SpeakerModel.SampleRate / samples.Length;
        var v = new float[8];
        v[Math.Clamp((int)(frequency / 200), 0, 7)] = 1;
        v[(Math.Clamp((int)(frequency / 200), 0, 7) + 1) % 8] = 0.2f; // a little shared component
        return SpeakerMath.Normalize(v);
    }

    public void Dispose()
    {
    }
}

public class SpeakerTrackingTests
{
    private static float[] Direction(int axis, float noise, Random random)
    {
        var v = new float[16];
        v[axis] = 1;
        for (int i = 0; i < v.Length; i++) v[i] += (float)(random.NextDouble() - 0.5) * noise;
        return SpeakerMath.Normalize(v);
    }

    [Fact]
    public void New_voice_needs_three_consistent_windows()
    {
        var tracker = new OnlineSpeakerTracker();
        var random = new Random(1);
        Assert.Null(tracker.Observe(Direction(0, 0.2f, random), 0.4));
        Assert.Null(tracker.Observe(Direction(0, 0.2f, random), 0.4));
        var first = tracker.Observe(Direction(0, 0.2f, random), 0.4);
        Assert.NotNull(first);
        Assert.Equal("Intervenant 1", first!.Name);
        Assert.Equal(1.2, first.TalkSeconds, 3);

        // One or two odd windows (cough, laugh, overlap) do not create a voice.
        Assert.Null(tracker.Observe(Direction(5, 0.2f, random), 0.4));
        Assert.Null(tracker.Observe(Direction(5, 0.2f, random), 0.4));
        Assert.Same(first, tracker.Observe(Direction(0, 0.2f, random), 0.4));
        Assert.Single(tracker.Speakers);
    }

    [Fact]
    public void A_voice_that_varies_stays_one_person()
    {
        // Real voices drift (intonation, codec): windows only ~0.55 alike with the voice so far.
        var tracker = new OnlineSpeakerTracker();
        var random = new Random(7);
        float[] Wobbly()
        {
            var v = new float[16];
            v[0] = 1;
            v[1 + random.Next(4)] = 0.8f + (float)random.NextDouble() * 0.6f;   // changing timbre
            return SpeakerMath.Normalize(v);
        }
        for (int i = 0; i < 60; i++) tracker.Observe(Wobbly(), 0.4);
        Assert.Single(tracker.Speakers);
    }

    [Fact]
    public void Giving_two_voices_the_same_name_merges_them()
    {
        var tracker = new OnlineSpeakerTracker();
        var random = new Random(8);
        for (int i = 0; i < 6; i++) tracker.Observe(Direction(0, 0.2f, random), 0.4);
        for (int i = 0; i < 4; i++) tracker.Observe(Direction(6, 0.2f, random), 0.4);
        Assert.Equal(2, tracker.Speakers.Count);

        Assert.True(tracker.Rename(1, "Paul"));
        Assert.True(tracker.Rename(2, "paul "));
        var paul = Assert.Single(tracker.Speakers);
        Assert.Equal(1, paul.Id);
        Assert.Equal("paul", paul.Name);
        Assert.Equal(4.0, paul.TalkSeconds, 3);
    }

    [Fact]
    public void Alternating_voices_are_told_apart_and_keep_their_names()
    {
        var tracker = new OnlineSpeakerTracker();
        var random = new Random(2);
        for (int turn = 0; turn < 6; turn++)
        {
            int axis = turn % 2 == 0 ? 0 : 3;
            for (int i = 0; i < 5; i++) tracker.Observe(Direction(axis, 0.3f, random), 0.4);
        }

        Assert.Equal(2, tracker.Speakers.Count);
        Assert.True(tracker.Rename(2, "Sarah"));
        Assert.Equal("Sarah", tracker.Observe(Direction(3, 0.3f, random), 0.4)!.Name);
        Assert.InRange(tracker.Speakers[0].TalkSeconds, 5.5, 6.5);
    }

    [Fact]
    public void Clustering_groups_similar_prints()
    {
        var random = new Random(3);
        var prints = new List<float[]>();
        int[] truth = [0, 1, 0, 2, 1, 1, 0, 2, 2, 0];
        foreach (int t in truth) prints.Add(Direction(t * 4, 0.35f, random));

        var labels = SpeakerClustering.Cluster(prints);

        Assert.Equal(3, labels.Distinct().Count());
        for (int i = 0; i < truth.Length; i++)
            for (int j = 0; j < truth.Length; j++)
                Assert.Equal(truth[i] == truth[j], labels[i] == labels[j]);
        Assert.Equal(0, labels[0]); // numbered by first appearance
    }

    [Fact]
    public void Tiny_groups_join_the_closest_voice()
    {
        var random = new Random(5);
        float[] Mix(int a, int b, float wb)
        {
            var v = new float[16];
            v[a] = 1;
            v[b] = wb;
            for (int i = 0; i < v.Length; i++) v[i] += (float)(random.NextDouble() - 0.5) * 0.1f;
            return SpeakerMath.Normalize(v);
        }
        // Two people (long sentences) and one short odd sentence (a laugh) a bit like the first.
        var prints = new List<float[]> { Mix(0, 1, 0.3f), Mix(8, 9, 0.3f), Mix(0, 1, 0.3f), Mix(4, 0, 0.45f), Mix(8, 9, 0.3f) };
        var durations = new List<double> { 5, 4, 6, 1.0, 3 };

        Assert.Equal(3, SpeakerClustering.Cluster(prints).Distinct().Count());
        var labels = SpeakerClustering.Cluster(prints, durations: durations);
        Assert.Equal([0, 1, 0, 0, 1], labels);
    }

    [Fact]
    public void Names_given_during_the_call_are_reused()
    {
        var random = new Random(4);
        var prints = new List<float[]> { Direction(0, 0.2f, random), Direction(8, 0.2f, random), Direction(0, 0.2f, random) };
        var clusters = SpeakerClustering.Cluster(prints);
        var live = new List<SpeakerProfile>
        {
            new(1, "Intervenant 1", false, Direction(4, 0f, random), 10), // nobody sounds like this one
            new(2, "Sarah", true, Direction(8, 0f, random), 30),
        };

        var (voices, names) = SpeakerClustering.Name(prints, clusters, live);

        Assert.Equal(2, voices[1]);
        Assert.Equal("Sarah", names[2]);
        Assert.Equal(voices[0], voices[2]);
        Assert.Equal(3, voices[0]); // new voice numbered after the live ones
        Assert.Equal("Intervenant 3", names[3]);
    }
}

public class VoicePipelineTests
{
    [Fact]
    public async Task Transcript_separates_the_other_participants_by_voice()
    {
        string root = TestPaths.TempDir();
        var folder = CallFolder.Create(root, new DateTime(2026, 11, 2, 10, 0, 0));
        folder.WriteMetadata(new CallMetadata { App = "Discord", Title = "Appel Discord", StartedAt = new DateTime(2026, 11, 2, 10, 0, 0), Status = CallStatus.Pending });

        // Two other participants (tones at 300 Hz and 1100 Hz) take turns; I speak once.
        using (var opus = new OpusRecordingWriter(folder.AudioPath, 32000, "Test"))
        using (var envelope = new EnvelopeWriter(folder.EnvelopePath))
        {
            var mixer = new CallMixer(0, new MuteGate(false), [opus, envelope], TimeSpan.Zero);
            var remote = mixer.AddTrack("app", TrackRole.Remote);
            var me = mixer.AddTrack("mic", TrackRole.Me);
            remote.Write(CodecTests.Sine(300, 0.3f, 48000 * 3), 48000 * 1);
            remote.Write(CodecTests.Sine(1100, 0.3f, 48000 * 3), 48000 * 6);
            me.Write(CodecTests.Sine(700, 0.3f, 48000 * 2), 48000 * 11);
            remote.Write(CodecTests.Sine(300, 0.3f, 48000 * 3), 48000 * 15);
            remote.PadTo(48000 * 20);
            mixer.Flush();
        }
        LiveSpeakerMonitor.SaveProfiles(folder.SpeakersPath, [new SpeakerProfile(1, "Léa", true, new ToneEmbedder().Embed(Tone(1100))!, 3)]);

        var embedder = new ToneEmbedder();
        var processor = new CallProcessor(() => new SegmentPerRegionTranscriber(), () => null, () => embedder);
        var result = await processor.ProcessAsync(folder, new ProcessingOptions("Moi", "Participants", "fake", null), null, CancellationToken.None);

        Assert.True(embedder.Calls >= 3);
        var paragraphs = TranscriptData.Read(folder.TranscriptDataPath);
        var voices = paragraphs.Where(p => p.Speaker == Speaker.Others).Select(p => p.Voice).ToList();
        Assert.Equal(3, voices.Count);
        Assert.Equal(voices[0], voices[2]);          // same 300 Hz voice
        Assert.NotEqual(voices[0], voices[1]);
        Assert.Equal(1, voices[1]);                  // the 1100 Hz voice was named "Léa" during the call
        Assert.Equal("Léa", result.Metadata.SpeakerNames[1]);

        string text = File.ReadAllText(folder.TranscriptPath);
        Assert.Contains("] Léa : phrase", text);
        Assert.Contains("] Intervenant 2 : phrase", text);
        Assert.Contains("] Moi : phrase", text);

        // Renaming afterwards rewrites the transcript and the report.
        Assert.True(CallOutputs.RenameVoice(folder, 2, "Hugo", "Moi", "Participants"));
        Assert.Contains("] Hugo : phrase", File.ReadAllText(folder.TranscriptPath));
        Assert.Contains("Hugo", File.ReadAllText(folder.ReportPath));

        // Giving "Léa" the name "hugo" says they are the same person: both voices merge.
        Assert.True(CallOutputs.RenameVoice(folder, 1, "hugo", "Moi", "Participants"));
        var merged = TranscriptData.Read(folder.TranscriptDataPath);
        Assert.Single(merged.Where(p => p.Speaker == Speaker.Others).Select(p => p.Voice).Distinct());
        Assert.Equal(2, merged.Count(p => p.Speaker == Speaker.Others));   // the first two paragraphs joined
        text = File.ReadAllText(folder.TranscriptPath);
        Assert.DoesNotContain("Léa", text);
        Assert.Contains("] hugo : phrase 1 phrase 2", text);
        Assert.Single(folder.ReadMetadata().SpeakerNames);
    }

    private static float[] Tone(double frequency) =>
        Enumerable.Range(0, SpeakerModel.SampleRate * 2).Select(i => 0.3f * (float)Math.Sin(2 * Math.PI * frequency * i / SpeakerModel.SampleRate)).ToArray();

    /// <summary>One segment per loud stretch of audio.</summary>
    internal sealed class SegmentPerRegionTranscriber : ITranscriber
    {
        private int _count;

        public async IAsyncEnumerable<RawSegment> TranscribeAsync(float[] samples, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            const int rate = SpeechPieceBuilder.SampleRate;
            int? start = null;
            for (int i = 0; i <= samples.Length; i += rate / 20)
            {
                bool loud = i < samples.Length && samples.Skip(i).Take(rate / 20).Max(Math.Abs) > 0.05f;
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
}

/// <summary>
/// With the real voice-print model (set ECHO_SPEAKER_MODEL, and ECHO_VOICES to a folder of
/// spk{speaker}_{n}.wav files): checks that different recorded voices are told apart and that
/// each person stays one voice. Use real people recorded at call quality (e.g. LibriSpeech
/// readers passed through Opus 16 then 24 kb/s): synthetic voices are far more regular than
/// real ones and give misleading thresholds. Skipped when they are not available.
/// </summary>
public class RealVoiceModelTests
{
    [Fact]
    public void Real_model_separates_recorded_voices()
    {
        string? model = Environment.GetEnvironmentVariable("ECHO_SPEAKER_MODEL");
        string? voices = Environment.GetEnvironmentVariable("ECHO_VOICES");
        if (model is null || voices is null || !File.Exists(model) || !Directory.Exists(voices)) return;

        using var embedder = new SherpaSpeakerEmbedder(model);
        var files = Directory.GetFiles(voices, "spk*.wav").OrderBy(f => f).ToList();
        var prints = files.Select(f => embedder.Embed(LoadWav16k(f))!).ToList();
        var truth = files.Select(f => Path.GetFileName(f)[..4]).ToList();

        var labels = SpeakerClustering.Cluster(prints);
        for (int i = 0; i < files.Count; i++)
            for (int j = 0; j < files.Count; j++)
                Assert.True(truth[i] == truth[j] == (labels[i] == labels[j]), $"{files[i]} / {files[j]}");

        // Live tracking over 1.6 s windows of the same files, voice after voice.
        var tracker = new OnlineSpeakerTracker();
        var assigned = new Dictionary<string, HashSet<int>>();
        foreach (var file in files)
        {
            var audio = LoadWav16k(file);
            for (int start = 0; start + 25600 <= audio.Length; start += 6400)
            {
                var speaker = tracker.Observe(embedder.Embed(audio[start..(start + 25600)])!, 0.4);
                if (speaker is null) continue;
                string key = Path.GetFileName(file)[..4];
                if (!assigned.TryGetValue(key, out var ids)) assigned[key] = ids = [];
                ids.Add(speaker.Id);
            }
        }
        Assert.Equal(truth.Distinct().Count(), tracker.Speakers.Count);
        Assert.All(assigned.Values, ids => Assert.Single(ids));
    }

    [Fact]
    public void Live_monitor_follows_who_speaks_in_real_time()
    {
        string? model = Environment.GetEnvironmentVariable("ECHO_SPEAKER_MODEL");
        string? voices = Environment.GetEnvironmentVariable("ECHO_VOICES");
        if (model is null || voices is null || !File.Exists(model) || !Directory.Exists(voices)) return;

        using var embedder = new SherpaSpeakerEmbedder(model);
        using var monitor = new LiveSpeakerMonitor(embedder);
        var seen = new List<(string Voice, int? Id)>();
        foreach (string voice in new[] { "spk0", "spk2", "spk0" })
        {
            // Two utterances of the voice, played at real speed (48 kHz, 100 ms packets).
            var audio = Directory.GetFiles(voices, $"{voice}_*.wav").OrderBy(f => f).Take(2).SelectMany(LoadWav48k).ToArray();
            for (int i = 0; i + 4800 <= audio.Length; i += 4800)
            {
                monitor.AddRemote(audio.AsSpan(i, 4800));
                Thread.Sleep(100);
                if (i > audio.Length / 2) seen.Add((voice, monitor.Snapshot().CurrentSpeakerId));
            }
        }

        var snapshot = monitor.Snapshot();
        Assert.Equal(2, snapshot.Speakers.Count);
        int first = seen.Where(s => s.Voice == "spk0" && s.Id is not null).GroupBy(s => s.Id).OrderByDescending(g => g.Count()).First().Key!.Value;
        int second = seen.Where(s => s.Voice == "spk2" && s.Id is not null).GroupBy(s => s.Id).OrderByDescending(g => g.Count()).First().Key!.Value;
        Assert.NotEqual(first, second);
        Assert.True(monitor.Rename(second, "Sarah"));
        Assert.Contains(monitor.Profiles(), p => p.Name == "Sarah" && p.Renamed);
    }

    private static float[] LoadWav48k(string path)
    {
        var resampler = new LinearResampler(SpeakerModel.SampleRate, AudioClock.SampleRate);
        return resampler.Process(LoadWav16k(path)).ToArray();
    }

    internal static float[] LoadWav16k(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        reader.BaseStream.Seek(24, SeekOrigin.Begin);
        int rate = reader.ReadInt32();
        reader.BaseStream.Seek(44, SeekOrigin.Begin);
        var samples = new List<float>();
        while (reader.BaseStream.Position < reader.BaseStream.Length - 1) samples.Add(reader.ReadInt16() / 32768f);
        var resampler = new LinearResampler(rate, SpeakerModel.SampleRate);
        return resampler.Process(samples.ToArray()).ToArray();
    }
}
