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

    /// <summary>Gives a recognised voice a name, and rewrites the transcript and the report.</summary>
    public static bool RenameVoice(CallFolder folder, int voice, string name, string myName, string othersName)
    {
        string clean = name.Trim();
        if (voice <= 0 || clean.Length == 0 || !File.Exists(folder.TranscriptDataPath)) return false;
        var metadata = folder.ReadMetadata();
        metadata.SpeakerNames[voice] = clean.Length > 40 ? clean[..40] : clean;
        folder.WriteMetadata(metadata);

        var paragraphs = TranscriptData.Read(folder.TranscriptDataPath);
        var naming = new SpeakerNaming(myName, othersName, metadata.SpeakerNames);
        WriteTranscript(folder, metadata, paragraphs, naming);
        WriteReport(folder, metadata, paragraphs, naming);
        return true;
    }
}
