using System.Text.Json;
using System.Text.Json.Serialization;

namespace Echo.Core.Output;

public enum CallStatus
{
    Recording,
    Pending,
    Processing,
    Done,
    Failed,
}

public sealed class CallMetadata
{
    /// <summary>Calling application ("Teams", "Discord").</summary>
    public string App { get; set; } = "Teams";

    public string Title { get; set; } = DefaultTitleFor("Teams");

    public DateTime StartedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    public double DurationSeconds { get; set; }

    public CallStatus Status { get; set; }

    public string? Error { get; set; }

    /// <summary>How the Teams mute state was known during the call (for transparency).</summary>
    public string? MuteSource { get; set; }

    public string? WhisperModel { get; set; }

    public string? SummaryProvider { get; set; }

    /// <summary>Names of the voices recognised in the call (1 → "Intervenant 1" or a name given by the user).</summary>
    public Dictionary<int, string> SpeakerNames { get; set; } = [];

    /// <summary>The user's name in this call when the application gave it (Discord), instead of the one in the settings.</summary>
    public string? MyName { get; set; }

    /// <summary>Where the participants' names come from: "discord" (exact) or "voices" (voice prints).</summary>
    public string? SpeakerSource { get; set; }

    [JsonIgnore]
    public bool HasExactNames => SpeakerSource == "discord";

    [JsonIgnore]
    public TimeSpan Duration => TimeSpan.FromSeconds(DurationSeconds);

    /// <summary>True while no real title is known (not from the app window nor the summary).</summary>
    [JsonIgnore]
    public bool HasDefaultTitle => Title == DefaultTitleFor(App);

    public static string DefaultTitleFor(string app) => $"Appel {app}";
}

/// <summary>One folder per call, e.g. "Documents\Echo\2026-10-30 14h05 - Point hebdo".</summary>
public sealed class CallFolder
{
    public const string AudioFileName = "appel.opus";
    public const string EnvelopeFileName = "appel.env";
    public const string TranscriptFileName = "transcription.txt";
    public const string SummaryFileName = "synthese.md";
    public const string ReportFileName = "compte-rendu.html";
    public const string MetadataFileName = "infos.json";
    public const string TranscriptDataFileName = "transcription.json";
    public const string SpeakersFileName = "voix.json";
    public const string DiscordFileName = Discord.DiscordCallData.FileName;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public CallFolder(string path)
    {
        Path = path;
    }

    public string Path { get; private set; }

    public string AudioPath => System.IO.Path.Combine(Path, AudioFileName);

    public string EnvelopePath => System.IO.Path.Combine(Path, EnvelopeFileName);

    public string TranscriptPath => System.IO.Path.Combine(Path, TranscriptFileName);

    /// <summary>Voice prints and names of the speakers met during the recording.</summary>
    public string SpeakersPath => System.IO.Path.Combine(Path, SpeakersFileName);

    /// <summary>Who was in the Discord call and when each person spoke (Discord connection only).</summary>
    public string DiscordPath => System.IO.Path.Combine(Path, DiscordFileName);

    /// <summary>Paragraphs as data, for the Echo window (hidden file).</summary>
    public string TranscriptDataPath => System.IO.Path.Combine(Path, TranscriptDataFileName);

    public string SummaryPath => System.IO.Path.Combine(Path, SummaryFileName);

    public string ReportPath => System.IO.Path.Combine(Path, ReportFileName);

    public string MetadataPath => System.IO.Path.Combine(Path, MetadataFileName);

    public static CallFolder Create(string root, DateTime startedAt)
    {
        string name = startedAt.ToString("yyyy-MM-dd HH'h'mm");
        string path = System.IO.Path.Combine(root, name);
        for (int i = 2; Directory.Exists(path); i++)
        {
            path = System.IO.Path.Combine(root, $"{name} ({i})");
        }
        Directory.CreateDirectory(path);
        return new CallFolder(path);
    }

    public CallMetadata ReadMetadata()
    {
        try
        {
            if (File.Exists(MetadataPath))
            {
                return JsonSerializer.Deserialize<CallMetadata>(File.ReadAllText(MetadataPath), JsonOptions) ?? new CallMetadata();
            }
        }
        catch (JsonException)
        {
        }
        return new CallMetadata { StartedAt = Directory.GetCreationTime(Path), Status = CallStatus.Pending };
    }

    public void WriteMetadata(CallMetadata metadata)
    {
        string temp = MetadataPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(metadata, JsonOptions));
        File.Move(temp, MetadataPath, overwrite: true);
    }

    /// <summary>Adds the meeting title to the folder name once the files are closed.</summary>
    public void RenameWithTitle(string title)
    {
        string clean = SanitizeFileName(title);
        if (clean.Length == 0) return;
        string parent = System.IO.Path.GetDirectoryName(Path)!;
        string target = System.IO.Path.Combine(parent, $"{System.IO.Path.GetFileName(Path)} - {clean}");
        if (Directory.Exists(target)) return;
        try
        {
            Directory.Move(Path, target);
            Path = target;
        }
        catch (IOException)
        {
            // Folder open in Explorer or locked: keep the date-only name.
        }
    }

    public static string SanitizeFileName(string text)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var chars = text.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray();
        string clean = new string(chars).Trim().TrimEnd('.');
        return clean.Length > 60 ? clean[..60].Trim() : clean;
    }

    /// <summary>Calls whose processing did not complete (app closed, crash, PC shut down…).</summary>
    public static IEnumerable<CallFolder> FindUnfinished(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var folder = new CallFolder(dir);
            if (!File.Exists(folder.MetadataPath) || !File.Exists(folder.AudioPath)) continue;
            var status = folder.ReadMetadata().Status;
            if (status is CallStatus.Recording or CallStatus.Pending or CallStatus.Processing)
            {
                yield return folder;
            }
        }
    }

    /// <summary>Most recent calls first.</summary>
    public static IEnumerable<(CallFolder Folder, CallMetadata Metadata)> List(string root, int max)
    {
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root)
            .Select(d => new CallFolder(d))
            .Where(f => File.Exists(f.MetadataPath))
            .Select(f => (f, f.ReadMetadata()))
            .OrderByDescending(x => x.Item2.StartedAt)
            .Take(max)
            .ToList();
    }
}
