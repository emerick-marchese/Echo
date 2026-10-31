using Echo.Core.Output;
using Echo.Core.Transcription;

namespace Echo.Tests;

public class RecordingLibraryTests
{
    private static CallFolder MakeCall(string root, DateTime startedAt, string title, string app, CallStatus status, bool withSummary = false)
    {
        var folder = CallFolder.Create(root, startedAt);
        folder.WriteMetadata(new CallMetadata { App = app, Title = title, StartedAt = startedAt, DurationSeconds = 125, Status = status });
        File.WriteAllBytes(folder.AudioPath, new byte[2048]);
        if (withSummary) File.WriteAllText(folder.SummaryPath, "# Titre\n\n## Résumé\nTexte [00:01:05]");
        return folder;
    }

    [Fact]
    public void Lists_recordings_newest_first()
    {
        string root = TestPaths.TempDir();
        MakeCall(root, new DateTime(2026, 10, 1, 9, 0, 0), "Ancien", "Teams", CallStatus.Done);
        MakeCall(root, new DateTime(2026, 10, 30, 18, 0, 0), "Raid", "Discord", CallStatus.Pending);
        Directory.CreateDirectory(Path.Combine(root, "dossier sans infos"));

        var list = new RecordingLibrary(root).List();

        Assert.Equal(["Raid", "Ancien"], list.Select(r => r.Title));
        Assert.Equal("Discord", list[0].App);
        Assert.Equal(2048, list[0].AudioBytes);
        Assert.Equal(CallStatus.Pending, list[0].Status);
    }

    [Fact]
    public void Detail_contains_summary_transcript_and_audio_path()
    {
        string root = TestPaths.TempDir();
        var folder = MakeCall(root, new DateTime(2026, 10, 30, 14, 5, 0), "Point hebdo", "Teams", CallStatus.Done, withSummary: true);
        TranscriptData.Write(folder.TranscriptDataPath,
        [
            new TranscriptParagraph(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(9), Speaker.Me, "Bonjour"),
            new TranscriptParagraph(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), Speaker.Others, "Salut"),
        ]);

        string id = Path.GetFileName(folder.Path);
        var detail = new RecordingLibrary(root).Get(id)!;

        Assert.True(detail.Summary.HasSummary);
        Assert.StartsWith("# Titre", detail.SummaryMarkdown);
        Assert.Equal(2, detail.Paragraphs.Count);
        Assert.Equal(Speaker.Me, detail.Paragraphs[0].Speaker);
        Assert.Equal($"{Uri.EscapeDataString(id)}/appel.opus", detail.AudioRelativePath);
    }

    [Fact]
    public void Older_recordings_are_read_from_the_text_transcript()
    {
        string root = TestPaths.TempDir();
        var folder = MakeCall(root, DateTime.Now, "Vieux", "Teams", CallStatus.Done);
        File.WriteAllText(folder.TranscriptPath, "Vieux\n30/10/2026 — durée 2 min\n\n[00:00:03] Moi : Bonjour\n[00:01:10] Participants : Salut à tous\n");

        var paragraphs = new RecordingLibrary(root).Get(Path.GetFileName(folder.Path))!.Paragraphs;

        Assert.Equal(2, paragraphs.Count);
        Assert.Equal(Speaker.Me, paragraphs[0].Speaker);
        Assert.Equal(TimeSpan.FromSeconds(70), paragraphs[0].End);
        Assert.Equal("Salut à tous", paragraphs[1].Text);
    }

    [Fact]
    public void Rename_changes_only_the_title()
    {
        string root = TestPaths.TempDir();
        var folder = MakeCall(root, DateTime.Now, "Appel Discord", "Discord", CallStatus.Done);
        var library = new RecordingLibrary(root);

        Assert.True(library.Rename(Path.GetFileName(folder.Path), "  Session du soir  "));
        Assert.Equal("Session du soir", folder.ReadMetadata().Title);
        Assert.False(library.Rename(Path.GetFileName(folder.Path), "   "));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../autre")]
    [InlineData("..\\autre")]
    [InlineData("C:\\Windows")]
    [InlineData("/etc")]
    [InlineData("")]
    public void Ids_cannot_escape_the_recordings_folder(string id)
    {
        string root = TestPaths.TempDir();
        Assert.Null(new RecordingLibrary(root).Find(id));
    }
}
