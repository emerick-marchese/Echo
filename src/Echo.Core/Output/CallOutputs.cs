using Echo.Core.Summary;
using Echo.Core.Transcription;

namespace Echo.Core.Output;

/// <summary>Writes the files of a call that depend on the speaker names (rewritten when voices are renamed).</summary>
public static class CallOutputs
{
    /// <summary>transcription.txt (readable) and transcription.json (for the Echo window).</summary>
    public static void WriteTranscript(CallFolder folder, CallMetadata metadata, IReadOnlyList<TranscriptParagraph> paragraphs, SpeakerNaming naming)
    {
        string header = $"{metadata.Title}\n{metadata.StartedAt:dd/MM/yyyy HH:mm} — durée {SummaryPrompt.FormatDuration(metadata.Duration)}\n\n";
        File.WriteAllText(folder.TranscriptPath, header + TranscriptFormatter.ToText(paragraphs, naming));
        TranscriptData.Write(folder.TranscriptDataPath, paragraphs);
    }

    /// <summary>compte-rendu.html, with the current summary (if any).</summary>
    public static void WriteReport(CallFolder folder, CallMetadata metadata, IReadOnlyList<TranscriptParagraph> paragraphs, SpeakerNaming naming)
    {
        string? summary = File.Exists(folder.SummaryPath) ? File.ReadAllText(folder.SummaryPath) : null;
        File.WriteAllText(folder.ReportPath, ReportWriter.Build(metadata, summary, summary is null ? metadata.Error : null, paragraphs, naming));
    }

    /// <summary>
    /// Gives a recognised voice a name, and rewrites the transcript and the report. Giving it
    /// the name of another voice merges both: they were the same person.
    /// </summary>
    public static bool RenameVoice(CallFolder folder, int voice, string name, string myName, string othersName)
    {
        string clean = name.Trim();
        if (voice <= 0 || clean.Length == 0 || !File.Exists(folder.TranscriptDataPath)) return false;
        clean = clean.Length > 40 ? clean[..40] : clean;
        var metadata = folder.ReadMetadata();
        IReadOnlyList<TranscriptParagraph> paragraphs = TranscriptData.Read(folder.TranscriptDataPath);

        var before = SpeakerNaming.For(metadata, myName, othersName);
        int same = paragraphs
            .Where(p => p.Speaker == Speaker.Others && p.Voice > 0 && p.Voice != voice)
            .Select(p => p.Voice)
            .Distinct()
            .FirstOrDefault(v => string.Equals(before.NameOf(Speaker.Others, v), clean, StringComparison.CurrentCultureIgnoreCase));
        if (same > 0)
        {
            paragraphs = TranscriptFormatter.Regroup(paragraphs.Select(p => p.Speaker == Speaker.Others && p.Voice == voice ? p with { Voice = same } : p));
            metadata.SpeakerNames.Remove(voice);
            metadata.SpeakerNames[same] = clean;
        }
        else
        {
            metadata.SpeakerNames[voice] = clean;
        }
        folder.WriteMetadata(metadata);

        var naming = SpeakerNaming.For(metadata, myName, othersName);
        WriteTranscript(folder, metadata, paragraphs, naming);
        WriteReport(folder, metadata, paragraphs, naming);
        return true;
    }
}
