using ListenTeams.Core.Audio;

namespace ListenTeams.Tests;

public class TimelineTrackTests
{
    [Fact]
    public void Gap_between_packets_is_filled_with_silence()
    {
        var track = new TimelineTrack("t", TrackRole.Remote);
        track.Write(Ones(480), 0);
        track.Write(Ones(480), 48000); // one second later

        Assert.Equal(48480, track.WritePosition);
        var buffer = new float[48480];
        track.Read(buffer);
        Assert.Equal(1f, buffer[0]);
        Assert.Equal(0f, buffer[24000]);
        Assert.Equal(1f, buffer[48000]);
    }

    [Fact]
    public void Small_jitter_is_tolerated_and_overlap_is_dropped()
    {
        var track = new TimelineTrack("t", TrackRole.Remote);
        track.Write(Ones(4800), 0);
        track.Write(Ones(4800), 4800 + 100);   // 2 ms late: appended as is
        Assert.Equal(9600, track.WritePosition);

        track.Write(Ones(4800), 9600 - 4000);  // 83 ms overlap: overlapping part dropped
        Assert.Equal(10400, track.WritePosition);
    }

    [Fact]
    public void PadTo_and_Read_keep_positions_consistent()
    {
        var track = new TimelineTrack("t", TrackRole.Me, startFrame: 1000);
        track.PadTo(1500);
        Assert.Equal(1500, track.WritePosition);
        track.Read(new float[200]);
        track.Write(Ones(10), 1500);
        Assert.Equal(1510, track.WritePosition);
    }

    [Fact]
    public void Buffer_grows_beyond_initial_capacity()
    {
        var track = new TimelineTrack("t", TrackRole.Remote);
        var big = Enumerable.Range(0, 48000 * 10).Select(i => (float)(i % 7)).ToArray();
        track.Write(big, 0);
        var read = new float[big.Length];
        track.Read(read);
        Assert.Equal(big, read);
    }

    internal static float[] Ones(int n) => Enumerable.Repeat(1f, n).ToArray();
}

public class MuteGateTests
{
    [Fact]
    public void Muted_microphone_is_silent_after_fade()
    {
        var gate = new MuteGate(initiallyMuted: false);
        gate.SetMuted(true, 1000);
        var samples = TimelineTrackTests.Ones(3000);
        gate.Apply(samples, 0);

        Assert.Equal(1f, samples[999]);
        Assert.True(samples[1200] < 1f);       // fading
        Assert.Equal(0f, samples[2000]);       // fully muted after 10 ms
    }

    [Fact]
    public void Initially_muted_gate_opens_on_unmute()
    {
        var gate = new MuteGate(initiallyMuted: true);
        Assert.True(gate.IsMuted);
        gate.SetMuted(false, 500);
        Assert.False(gate.IsMuted);
        var samples = TimelineTrackTests.Ones(2000);
        gate.Apply(samples, 0);
        Assert.Equal(0f, samples[400]);
        Assert.Equal(1f, samples[1500]);
    }

    [Fact]
    public void Events_are_applied_across_blocks()
    {
        var gate = new MuteGate(false);
        gate.SetMuted(true, 4800);
        var first = TimelineTrackTests.Ones(4800);
        var second = TimelineTrackTests.Ones(4800);
        gate.Apply(first, 0);
        gate.Apply(second, 4800);
        Assert.All(first, s => Assert.Equal(1f, s));
        Assert.Equal(0f, second[1000]);
    }
}

public class CallMixerTests
{
    private sealed class CaptureSink : IMixerSink
    {
        public List<float> Mixed { get; } = [];
        public List<float> Me { get; } = [];
        public List<float> Remote { get; } = [];

        public void OnMixed(ReadOnlySpan<float> mixed, ReadOnlySpan<float> me, ReadOnlySpan<float> remote)
        {
            Mixed.AddRange(mixed.ToArray());
            Me.AddRange(me.ToArray());
            Remote.AddRange(remote.ToArray());
        }
    }

    [Fact]
    public void Tracks_are_aligned_on_timestamps_and_microphone_is_gated()
    {
        var sink = new CaptureSink();
        var gate = new MuteGate(initiallyMuted: false);
        var mixer = new CallMixer(0, gate, [sink], TimeSpan.Zero);
        var remote = mixer.AddTrack("teams", TrackRole.Remote);
        var me = mixer.AddTrack("mic", TrackRole.Me);

        // Remote speaks during [0.5 s, 0.6 s], microphone during [0.2 s, 0.3 s] and [0.8 s, 0.9 s].
        remote.Write(Constant(4800, 0.25f), 24000);
        me.Write(Constant(4800, 0.5f), 9600);
        me.Write(Constant(4800, 0.5f), 38400);
        gate.SetMuted(true, 36000); // muted in Teams before the second sentence

        mixer.Flush();

        Assert.Equal(43200, sink.Mixed.Count);
        Assert.Equal(0.5f, sink.Me[12000], 3);
        Assert.Equal(0.25f, sink.Remote[26000], 3);
        Assert.Equal(0f, sink.Remote[12000]);
        Assert.Equal(0f, sink.Me[40000]);      // spoken while muted in Teams: not recorded
        Assert.Equal(0f, sink.Mixed[40000]);
    }

    [Fact]
    public void Process_stays_behind_real_time_by_the_latency()
    {
        var sink = new CaptureSink();
        var mixer = new CallMixer(0, new MuteGate(false), [sink], TimeSpan.FromSeconds(1));
        mixer.AddTrack("teams", TrackRole.Remote);
        mixer.Process(now100ns: 30_000_000); // 3 s after start
        Assert.Equal(2 * AudioClock.SampleRate, mixer.MixedFrames);
    }

    [Fact]
    public void Mix_is_soft_clipped()
    {
        var sink = new CaptureSink();
        var mixer = new CallMixer(0, new MuteGate(false), [sink], TimeSpan.Zero);
        mixer.AddTrack("a", TrackRole.Remote).Write(Constant(100, 0.9f), 0);
        mixer.AddTrack("b", TrackRole.Me).Write(Constant(100, 0.9f), 0);
        mixer.Flush();
        Assert.All(sink.Mixed, s => Assert.InRange(s, 0.9f, 1f));
    }

    private static float[] Constant(int n, float value) => Enumerable.Repeat(value, n).ToArray();
}

public class CodecTests
{
    [Fact]
    public void Opus_file_round_trip_keeps_duration_and_level()
    {
        string dir = TestPaths.TempDir();
        string path = Path.Combine(dir, "test.opus");
        var tone = Sine(440, 0.3f, AudioClock.SampleRate * 3);

        using (var writer = new OpusRecordingWriter(path, 24000, "test"))
        {
            for (int i = 0; i < tone.Length; i += 4800)
            {
                var block = tone.AsSpan(i, Math.Min(4800, tone.Length - i));
                writer.OnMixed(block, block, block);
            }
            writer.Finish();
        }

        long size = new FileInfo(path).Length;
        Assert.InRange(size, 3_000, 20_000); // ≈ 24 kb/s × 3 s

        var decoded = OpusFileDecoder.Decode(path).SelectMany(b => b).ToArray();
        Assert.InRange(decoded.Length, tone.Length - 4800, tone.Length + 4800);
        double rms = Math.Sqrt(decoded.Skip(4800).Take(48000).Average(s => s * s));
        Assert.InRange(rms, 0.15, 0.27); // 0.3 / √2 ≈ 0.21
    }

    [Fact]
    public void Decimator_keeps_speech_band_and_removes_high_frequencies()
    {
        var low = Decimate(Sine(1000, 0.5f, 48000));
        var high = Decimate(Sine(11000, 0.5f, 48000));

        Assert.InRange(low.Count, 15990, 16010);
        Assert.InRange(Rms(low.Skip(200)), 0.33, 0.38);
        Assert.True(Rms(high.Skip(200)) < 0.01);
    }

    [Fact]
    public void Decimator_is_continuous_across_odd_block_sizes()
    {
        var tone = Sine(500, 0.5f, 48000);
        var whole = Decimate(tone);

        var decimator = new Decimator();
        var pieces = new List<float>();
        int[] sizes = [7, 960, 1, 4801, 333];
        int offset = 0, k = 0;
        while (offset < tone.Length)
        {
            int n = Math.Min(sizes[k++ % sizes.Length], tone.Length - offset);
            decimator.Process(tone.AsSpan(offset, n), pieces);
            offset += n;
        }

        Assert.Equal(whole.Count, pieces.Count);
        for (int i = 0; i < whole.Count; i++) Assert.Equal(whole[i], pieces[i], 5);
    }

    [Fact]
    public void Sample_converter_downmixes_pcm16_stereo()
    {
        var converter = new SampleConverter(new CaptureFormat(48000, 2, SampleEncoding.Pcm16));
        var bytes = new byte[8];
        BitConverter.GetBytes((short)16384).CopyTo(bytes, 0);  // L = 0.5
        BitConverter.GetBytes((short)0).CopyTo(bytes, 2);      // R = 0
        BitConverter.GetBytes((short)-32768).CopyTo(bytes, 4); // L = -1
        BitConverter.GetBytes((short)-32768).CopyTo(bytes, 6); // R = -1
        var mono = converter.ToMono48k(bytes, silent: false).ToArray();
        Assert.Equal([0.25f, -1f], mono);
    }

    [Fact]
    public void Sample_converter_resamples_44100_to_48000()
    {
        var converter = new SampleConverter(new CaptureFormat(44100, 1, SampleEncoding.Float32));
        var input = Sine(440, 0.5f, 44100, 44100);
        var bytes = new byte[input.Length * 4];
        Buffer.BlockCopy(input, 0, bytes, 0, bytes.Length);
        int total = 0;
        for (int i = 0; i < bytes.Length; i += 4410 * 4)
        {
            total += converter.ToMono48k(bytes.AsSpan(i, Math.Min(4410 * 4, bytes.Length - i)), false).Length;
        }
        Assert.InRange(total, 47990, 48010);
    }

    private static List<float> Decimate(float[] input)
    {
        var output = new List<float>();
        new Decimator().Process(input, output);
        return output;
    }

    internal static float[] Sine(double frequency, float amplitude, int count, int rate = AudioClock.SampleRate) =>
        Enumerable.Range(0, count).Select(i => amplitude * (float)Math.Sin(2 * Math.PI * frequency * i / rate)).ToArray();

    private static double Rms(IEnumerable<float> samples) => Math.Sqrt(samples.Average(s => (double)s * s));
}

internal static class TestPaths
{
    public static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "listenteams-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
