using System.Text;
using System.Text.RegularExpressions;
using ListenTeams.Core.Audio;

namespace ListenTeams.Core.Transcription;

public enum Speaker
{
    Me,
    Others,
}

/// <summary>A segment as returned by the speech recogniser, relative to the audio it was given.</summary>
public readonly record struct RawSegment(TimeSpan Start, TimeSpan End, string Text, float Probability);

/// <summary>A recognised segment placed on the call timeline.</summary>
public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text, Speaker Speaker = Speaker.Others);

/// <summary>Consecutive segments of the same speaker, shown as one paragraph.</summary>
public sealed record TranscriptParagraph(TimeSpan Start, TimeSpan End, Speaker Speaker, string Text);

public static class SpeakerAttributor
{
    /// <summary>
    /// Labels each segment with whoever was loudest during it: the (Teams-gated)
    /// microphone or what Teams played.
    /// </summary>
    public static List<TranscriptSegment> Attribute(IEnumerable<TranscriptSegment> segments, Envelope envelope)
    {
        var result = new List<TranscriptSegment>();
        Speaker previous = Speaker.Others;
        foreach (var segment in segments)
        {
            var (me, remote) = envelope.EnergyBetween(segment.Start, segment.End);
            Speaker speaker = me <= 1e-12 && remote <= 1e-12 ? previous
                : me >= remote ? Speaker.Me
                : Speaker.Others;
            result.Add(segment with { Speaker = speaker });
            previous = speaker;
        }
        return result;
    }
}

/// <summary>Removes the classic Whisper hallucinations and repetition loops.</summary>
public static partial class HallucinationFilter
{
    private static readonly string[] KnownPhrases =
    [
        "sous-titres réalisés par",
        "sous-titrage st'",
        "sous-titrage société radio-canada",
        "sous-titrage fr",
        "amara.org",
        "merci d'avoir regardé",
        "abonnez-vous",
        "thanks for watching",
        "thank you for watching",
        "subtitles by",
        "transcribed by",
        "untertitel im auftrag",
    ];

    public static List<TranscriptSegment> Filter(IEnumerable<TranscriptSegment> segments)
    {
        var result = new List<TranscriptSegment>();
        string? lastText = null;
        int repeats = 0;
        foreach (var segment in segments)
        {
            string text = Clean(segment.Text);
            if (text.Length == 0) continue;

            string lower = text.ToLowerInvariant();
            if (KnownPhrases.Any(lower.Contains)) continue;

            if (string.Equals(lower, lastText, StringComparison.Ordinal))
            {
                if (++repeats >= 2) continue; // keep at most two identical segments in a row
            }
            else
            {
                repeats = 0;
                lastText = lower;
            }

            result.Add(segment with { Text = text });
        }
        return result;
    }

    internal static string Clean(string text)
    {
        // Drop sound annotations like [Musique], (applaudissements), *rires*, [BLANK_AUDIO].
        text = AnnotationRegex().Replace(text, " ");
        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text.Any(char.IsLetterOrDigit) ? text : string.Empty;
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*|♪+")]
    private static partial Regex AnnotationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

public static class TranscriptFormatter
{
    public static List<TranscriptParagraph> ToParagraphs(IReadOnlyList<TranscriptSegment> segments, TimeSpan? maxGap = null)
    {
        var gap = maxGap ?? TimeSpan.FromSeconds(3);
        var paragraphs = new List<TranscriptParagraph>();
        var text = new StringBuilder();
        TranscriptSegment? first = null;
        TranscriptSegment? last = null;

        foreach (var segment in segments)
        {
            bool sameParagraph = last is not null
                && segment.Speaker == last.Speaker
                && segment.Start - last.End <= gap
                && text.Length < 700;
            if (!sameParagraph && first is not null)
            {
                paragraphs.Add(new TranscriptParagraph(first.Start, last!.End, first.Speaker, text.ToString()));
                text.Clear();
                first = null;
            }

            first ??= segment;
            if (text.Length > 0) text.Append(' ');
            text.Append(segment.Text);
            last = segment;
        }

        if (first is not null)
        {
            paragraphs.Add(new TranscriptParagraph(first.Start, last!.End, first.Speaker, text.ToString()));
        }
        return paragraphs;
    }

    public static string Timestamp(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    public static string SpeakerName(Speaker speaker, string myName, string othersName) =>
        speaker == Speaker.Me ? myName : othersName;

    /// <summary>"[00:01:23] Moi : …" lines, one paragraph per line.</summary>
    public static string ToText(IEnumerable<TranscriptParagraph> paragraphs, string myName, string othersName)
    {
        var sb = new StringBuilder();
        foreach (var p in paragraphs)
        {
            sb.Append('[').Append(Timestamp(p.Start)).Append("] ")
              .Append(SpeakerName(p.Speaker, myName, othersName)).Append(" : ")
              .AppendLine(p.Text);
        }
        return sb.ToString();
    }
}
