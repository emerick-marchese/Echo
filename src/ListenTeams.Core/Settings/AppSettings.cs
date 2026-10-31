using System.Text.Json;
using System.Text.Json.Serialization;
using ListenTeams.Core.Apps;

namespace ListenTeams.Core.Settings;

public enum SummaryProvider
{
    /// <summary>Claude (Anthropic API): best quality, needs an API key and Internet.</summary>
    Claude,

    /// <summary>A local model served by Ollama: nothing leaves the PC.</summary>
    Ollama,

    /// <summary>Transcription only.</summary>
    None,
}

public sealed class AppSettings
{
    public static string DefaultOutputFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ListenTeams");

    /// <summary>Record Microsoft Teams calls.</summary>
    public bool RecordTeams { get; set; } = true;

    /// <summary>Record Discord voice calls.</summary>
    public bool RecordDiscord { get; set; } = true;

    /// <summary>Folder where every call gets its own sub-folder.</summary>
    public string OutputFolder { get; set; } = DefaultOutputFolder;

    /// <summary>Opus bitrate in bits/s. 24 kb/s ≈ 11 MB per hour, near-transparent for speech.</summary>
    public int OpusBitrate { get; set; } = 24000;

    /// <summary>Whisper model file name; empty = best model found in the models folder.</summary>
    public string WhisperModel { get; set; } = string.Empty;

    /// <summary>Two-letter language code, or "auto".</summary>
    public string Language { get; set; } = "fr";

    /// <summary>Use the GPU (Vulkan) for transcription when available.</summary>
    public bool UseGpu { get; set; } = true;

    /// <summary>Names, acronyms, jargon: helps Whisper spell them correctly.</summary>
    public string Vocabulary { get; set; } = string.Empty;

    public string MyName { get; set; } = "Moi";

    public string OthersName { get; set; } = "Participants";

    public SummaryProvider SummaryProvider { get; set; } = SummaryProvider.Claude;

    /// <summary>Anthropic API key, encrypted for the current Windows user.</summary>
    public string? ClaudeApiKeyProtected { get; set; }

    public string ClaudeModel { get; set; } = "claude-opus-5-5";

    public string OllamaUrl { get; set; } = "http://localhost:11434";

    public string OllamaModel { get; set; } = "mistral-nemo";

    /// <summary>Capture device id; empty = Windows default communications microphone.</summary>
    public string MicrophoneDeviceId { get; set; } = string.Empty;

    /// <summary>Ask Windows to remove the loudspeaker echo from the microphone.</summary>
    public bool EchoCancellation { get; set; } = true;

    /// <summary>When the mute state in Teams/Discord cannot be determined: record the microphone anyway?</summary>
    public bool RecordMicWhenMuteUnknown { get; set; }

    /// <summary>How far back a mute change is applied, to cover the delay before Teams reports it.</summary>
    public int MuteLookBackMs { get; set; } = 250;

    /// <summary>Token given by Teams once the user allowed ListenTeams (local API pairing).</summary>
    public string? TeamsApiToken { get; set; }

    public bool PairingRequestedOnce { get; set; }

    public bool NotifyOnCallStart { get; set; } = true;

    public bool IsEnabled(CallApp app) => app switch
    {
        CallApp.Teams => RecordTeams,
        CallApp.Discord => RecordDiscord,
        _ => false,
    };

    public void SetEnabled(CallApp app, bool enabled)
    {
        if (app == CallApp.Teams) RecordTeams = enabled;
        else if (app == CallApp.Discord) RecordDiscord = enabled;
    }
}

/// <summary>Encrypts secrets at rest (DPAPI on Windows).</summary>
public interface ISecretProtector
{
    string Protect(string secret);

    string? Unprotect(string protectedSecret);
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly object _lock = new();

    public SettingsStore(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? Changed;

    public string? ClaudeApiKey
    {
        get
        {
            var key = Current.ClaudeApiKeyProtected;
            if (!string.IsNullOrEmpty(key)) return _protector.Unprotect(key);
            var env = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            return string.IsNullOrWhiteSpace(env) ? null : env;
        }
    }

    public void SetClaudeApiKey(AppSettings settings, string? apiKey) =>
        settings.ClaudeApiKeyProtected = string.IsNullOrWhiteSpace(apiKey) ? null : _protector.Protect(apiKey.Trim());

    public void Update(Action<AppSettings> change)
    {
        AppSettings snapshot;
        lock (_lock)
        {
            snapshot = Clone(Current);
            change(snapshot);
            Save(snapshot);
            Current = snapshot;
        }
        Changed?.Invoke(snapshot);
    }

    public AppSettings CloneCurrent() => Clone(Current);

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Paramètres illisibles, valeurs par défaut utilisées : {ex.Message}");
        }
        return new AppSettings();
    }

    private void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }

    private static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!;
}
