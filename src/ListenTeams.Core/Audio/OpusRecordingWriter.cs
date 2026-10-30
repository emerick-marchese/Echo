using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;

namespace ListenTeams.Core.Audio;

/// <summary>
/// Streams the mixed call to an Ogg/Opus file. Opus in voice mode gives near-transparent
/// speech at 24 kb/s (about 11 MB per hour), playable by Windows, VLC and every browser.
/// The file is written progressively, so a crash still leaves a playable recording.
/// </summary>
public sealed class OpusRecordingWriter : IMixerSink, IDisposable
{
    private readonly FileStream _file;
    private readonly OpusOggWriteStream _ogg;
    private short[] _pcm = new short[AudioClock.SampleRate];
    private bool _finished;

    public OpusRecordingWriter(string path, int bitrate, string title)
    {
        IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(AudioClock.SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = Math.Clamp(bitrate, 8000, 128000);
        encoder.UseVBR = true;
        encoder.Complexity = 8;
        encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;

        var tags = new OpusTags();
        tags.Fields["TITLE"] = title;
        tags.Fields["DATE"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        tags.Fields["ENCODER"] = "ListenTeams";

        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        _ogg = new OpusOggWriteStream(encoder, _file, tags, AudioClock.SampleRate);
    }

    public void OnMixed(ReadOnlySpan<float> mixed, ReadOnlySpan<float> me, ReadOnlySpan<float> remote)
    {
        if (_finished || mixed.IsEmpty) return;
        if (_pcm.Length < mixed.Length) _pcm = new short[mixed.Length];
        for (int i = 0; i < mixed.Length; i++)
        {
            _pcm[i] = (short)Math.Clamp((int)MathF.Round(mixed[i] * 32767f), short.MinValue, short.MaxValue);
        }
        _ogg.WriteSamples(_pcm, 0, mixed.Length);
    }

    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        _ogg.Finish();
        _file.Dispose();
    }

    public void Dispose() => Finish();
}

/// <summary>Decodes an Ogg/Opus file back to mono 48 kHz float blocks.</summary>
public static class OpusFileDecoder
{
    public static IEnumerable<float[]> Decode(string path)
    {
        using var file = File.OpenRead(path);
        IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(AudioClock.SampleRate, 1);
        var ogg = new OpusOggReadStream(decoder, file);
        while (ogg.HasNextPacket)
        {
            short[]? packet;
            try
            {
                packet = ogg.DecodeNextPacket();
            }
            catch (Exception)
            {
                // Truncated last page (e.g. the app was killed during a call): stop here.
                yield break;
            }

            if (packet is null || packet.Length == 0) continue;
            var block = new float[packet.Length];
            for (int i = 0; i < packet.Length; i++) block[i] = packet[i] / 32768f;
            yield return block;
        }
    }
}
