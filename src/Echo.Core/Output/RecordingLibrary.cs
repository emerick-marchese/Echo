using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Echo.Core.Transcription;

namespace Echo.Core.Output;

/// <summary>A recording as listed in the Echo window.</summary>
public sealed record RecordingSummary(
    string Id,
    string Title,
    string App,
    DateTime StartedAt,
    double DurationSeconds,
    CallStatus Status,
    string? Error,
    bool HasSummary,
    long AudioBytes);

/// <summary>Everything the detail view of a recording shows.</summary>
public sealed record RecordingDetail(
    RecordingSummary Summary,
    string? SummaryMarkdown,
    IReadOnlyList<TranscriptParagraph> Paragraphs,
    string? AudioRelativePath,
    string FolderPath,
    string? MuteSource,
    string? WhisperModel,
    string? SummaryProvider,
    IReadOnlyDictionary<int, string> SpeakerNames);

/// <summary>Reads and manages the recordings folder (one sub-folder per call).</summary>
public sealed class RecordingLibrary
{
    public RecordingLibrary(string root)
    {
        Root = System.IO.Path.GetFullPath(root);
    }

    public string Root { get; }

    public IReadOnlyList<RecordingSummary> List(int max = 500)
    {
        if (!Directory.Exists(Root)) return [];
        return Directory.EnumerateDirectories(Root)
            .Select(d => new CallFolder(d))
            .Where(f => File.Exists(f.MetadataPath))
            .Select(ToSummary)
            .OrderByDescending(r => r.StartedAt)
            .Take(max)
            .ToList();
    }

    public RecordingDetail? Get(string id)
    {
        var folder = Find(id);
        if (folder is null) return null;
        var metadata = folder.ReadMetadata();
        return new RecordingDetail(
            ToSummary(folder),
            File.Exists(folder.SummaryPath) ? File.ReadAllText(folder.SummaryPath) : null,
            ReadParagraphs(folder),
            File.Exists(folder.AudioPath) ? $"{Uri.EscapeDataString(id)}/{CallFolder.AudioFileName}" : null,
            folder.Path,
            metadata.MuteSource,
            metadata.WhisperModel,
            metadata.SummaryProvider,
            metadata.SpeakerNames);
    }

    /// <summary>Resolves a recording id (its folder name) safely: no path may escape the root.</summary>
    public CallFolder? Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Contains('/') || id.Contains('\\') || id is "." or "..") return null;
        string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, id));
        if (!string.Equals(System.IO.Path.GetDirectoryName(path), Root.TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return null;
        var folder = new CallFolder(path);
        return File.Exists(folder.MetadataPath) ? folder : null;
    }

    public bool Rename(string id, string title)
    {
        var folder = Find(id);
        string clean = title.Trim();
        if (folder is null || clean.Length == 0) return false;
        var metadata = folder.ReadMetadata();
        metadata.Title = clean.Length > 120 ? clean[..120] : clean;
        folder.WriteMetadata(metadata);
        return true;
    }

    private static RecordingSummary ToSummary(CallFolder folder)
    {
        var metadata = folder.ReadMetadata();
        long audio = File.Exists(folder.AudioPath) ? new FileInfo(folder.AudioPath).Length : 0;
        return new RecordingSummary(
            System.IO.Path.GetFileName(folder.Path),
            metadata.Title,
            metadata.App,
            metadata.StartedAt,
            metadata.DurationSeconds,
            metadata.Status,
            metadata.Error,
            File.Exists(folder.SummaryPath),
            audio);
    }

    private static IReadOnlyList<TranscriptParagraph> ReadParagraphs(CallFolder folder)
    {
        if (File.Exists(folder.TranscriptDataPath))
        {
            try
            {
                return TranscriptData.Read(folder.TranscriptDataPath);
            }
            catch (JsonException)
            {
            }
        }
        return File.Exists(folder.TranscriptPath) ? TranscriptData.ParseText(File.ReadAllText(folder.TranscriptPath)) : [];
    }
}

/// <summary>Stores transcript paragraphs as JSON; also reads the text format of older recordings.</summary>
public static partial class TranscriptData
{
    private sealed record Item(double Start, double End, string Speaker, string Text, int Voice = 0);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static void Write(string path, IEnumerable<TranscriptParagraph> paragraphs)
    {
        var items = paragraphs.Select(p => new Item(p.Start.TotalSeconds, p.End.TotalSeconds, p.Speaker == Speaker.Me ? "me" : "others", p.Text, p.Voice));
        File.WriteAllText(path, JsonSerializer.Serialize(items, Options));
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch (IOException)
        {
        }
    }

    public static IReadOnlyList<TranscriptParagraph> Read(string path)
    {
        var items = JsonSerializer.Deserialize<List<Item>>(File.ReadAllText(path), Options) ?? [];
        return items.Select(i => new TranscriptParagraph(
            TimeSpan.FromSeconds(i.Start), TimeSpan.FromSeconds(i.End),
            i.Speaker == "me" ? Speaker.Me : Speaker.Others, i.Text, i.Voice)).ToList();
    }

    /// <summary>Parses "[00:01:23] Name : text" lines; a paragraph is "me" when spoken by <paramref name="myName"/>.</summary>
    public static IReadOnlyList<TranscriptParagraph> ParseText(string text, string myName = "Moi")
    {
        var result = new List<TranscriptParagraph>();
        foreach (Match m in LineRegex().Matches(text))
        {
            var start = new TimeSpan(
                int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
            var speaker = m.Groups[4].Value.Trim() == myName ? Speaker.Me : Speaker.Others;
            result.Add(new TranscriptParagraph(start, start, speaker, m.Groups[5].Value.Trim()));
        }
        // End of each paragraph = start of the next one.
        for (int i = 0; i < result.Count - 1; i++) result[i] = result[i] with { End = result[i + 1].Start };
        return result;
    }

    [GeneratedRegex(@"^\[(\d+):(\d{2}):(\d{2})\] (.+?) : (.*)$", RegexOptions.Multiline)]
    private static partial Regex LineRegex();
}
