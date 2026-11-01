using System.Text.Json;
using System.Text.RegularExpressions;
using Echo.Core;
using Echo.Core.Apps;
using Echo.Core.Discord;
using Echo.Core.Output;
using Echo.Core.Settings;
using Echo.Core.Teams;
using Echo.Core.Transcription;
using Echo.Platform;
using Microsoft.VisualBasic.FileIO;
using NAudio.CoreAudioApi;

namespace Echo.Ui;

/// <summary>
/// Handles the requests of the web interface ({ id, type, args } → { id, ok, result | error })
/// and builds the data it shows. Runs on the UI thread.
/// </summary>
internal sealed class UiBridge
{
    public const string MediaHost = "media.echo";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly (string Code, string Name)[] Languages =
    [
        ("fr", "Français"), ("en", "English"), ("auto", "Détection automatique"),
        ("de", "Deutsch"), ("es", "Español"), ("it", "Italiano"), ("nl", "Nederlands"), ("pt", "Português"),
    ];

    private readonly SettingsStore _settings;
    private readonly CallCoordinator _coordinator;
    private readonly IWin32Window _owner;

    public UiBridge(SettingsStore settings, CallCoordinator coordinator, IWin32Window owner)
    {
        _settings = settings;
        _coordinator = coordinator;
        _owner = owner;
    }

    /// <summary>Raised when the theme changes (the window updates its title bar).</summary>
    public event Action<string>? ThemeChanged;

    /// <summary>Raised when the recordings folder changes (the window remaps media.echo).</summary>
    public event Action<string>? OutputFolderChanged;

    public string StartPage { get; set; } = "record";

    public async Task<string> HandleAsync(string message)
    {
        int id = 0;
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetInt32();
            string type = root.GetProperty("type").GetString() ?? string.Empty;
            var args = root.TryGetProperty("args", out var a) ? a.Clone() : default;
            object? result = await DispatchAsync(type, args).ConfigureAwait(true);
            return JsonSerializer.Serialize(new { id, ok = true, result }, Json);
        }
        catch (Exception ex)
        {
            Log.Warn($"Interface : {ex.Message}");
            return JsonSerializer.Serialize(new { id, ok = false, error = ex.Message }, Json);
        }
    }

    public static string Event(string name, object? data) => JsonSerializer.Serialize(new { @event = name, data }, Json);

    private async Task<object?> DispatchAsync(string type, JsonElement args)
    {
        var library = new RecordingLibrary(_settings.Current.OutputFolder);
        switch (type)
        {
            case "app.info":
                return new { version = typeof(UiBridge).Assembly.GetName().Version?.ToString(3), startPage = StartPage };
            case "state":
                return BuildState();
            case "app.select":
                _settings.Update(s => s.SelectedApp = ParseApp(Str(args, "app")));
                return null;
            case "recording.start":
                return await _coordinator.StartRecordingAsync(ParseApp(Str(args, "app"))).ConfigureAwait(true);
            case "recording.stop":
                await _coordinator.StopRecordingAsync().ConfigureAwait(true);
                return null;
            case "speakers.renameLive":
                return _coordinator.RenameLiveSpeaker(args.TryGetProperty("id", out var sid) ? sid.GetInt32() : 0, Str(args, "name"));
            case "recording.setAuto":
                _settings.Update(s =>
                {
                    if (args.TryGetProperty("autoRecord", out var auto)) s.AutoRecord = auto.GetBoolean();
                    if (args.TryGetProperty("autoStop", out var stop)) s.AutoStopAtCallEnd = stop.GetBoolean();
                });
                return null;

            case "recordings.list":
                return library.List().Select(r => ToDto(r)).ToList();
            case "recordings.get":
                return BuildDetail(library, Str(args, "id"));
            case "recordings.rename":
                return library.Rename(Str(args, "id"), Str(args, "title"));
            case "recordings.renameVoice":
            {
                var folder = Require(library, Str(args, "id"));
                EnsureNotBusy(folder);
                int voice = args.TryGetProperty("voice", out var v) ? v.GetInt32() : 0;
                var s = _settings.Current;
                return CallOutputs.RenameVoice(folder, voice, Str(args, "name"), s.MyName, s.OthersName);
            }
            case "recordings.resummarize":
            {
                var folder = Require(library, Str(args, "id"));
                EnsureNotBusy(folder);
                _coordinator.Processing.EnqueueSummary(folder);
                return true;
            }
            case "recordings.reprocess":
            {
                var folder = Require(library, Str(args, "id"));
                if (!File.Exists(folder.AudioPath)) throw new InvalidOperationException("Fichier audio introuvable.");
                EnsureNotBusy(folder);
                var metadata = folder.ReadMetadata();
                metadata.Status = CallStatus.Pending;
                metadata.Error = null;
                folder.WriteMetadata(metadata);
                _coordinator.Processing.Enqueue(folder);
                return true;
            }
            case "recordings.delete":
            {
                var folder = Require(library, Str(args, "id"));
                EnsureNotBusy(folder);
                FileSystem.DeleteDirectory(folder.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                return true;
            }
            case "recordings.openFolder":
                AppPaths.Open(Require(library, Str(args, "id")).Path);
                return null;
            case "recordings.openReport":
                AppPaths.Open(Require(library, Str(args, "id")).ReportPath);
                return null;

            case "settings.get":
                return BuildSettings();
            case "settings.options":
                return BuildOptions();
            case "settings.save":
                SaveSettings(args);
                return BuildSettings();
            case "settings.setTheme":
                string theme = Str(args, "theme");
                _settings.Update(s => s.Theme = theme);
                ThemeChanged?.Invoke(theme);
                return null;
            case "settings.browseFolder":
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = "Dossier des enregistrements",
                    UseDescriptionForTitle = true,
                    SelectedPath = Str(args, "current"),
                };
                return dialog.ShowDialog(_owner) == DialogResult.OK ? dialog.SelectedPath : null;
            }

            case "teams.pair":
                return await _coordinator.RequestPairingAsync().ConfigureAwait(true);
            case "discord.connect":
                return await ConnectDiscordAsync(Str(args, "clientId").Trim(), Str(args, "clientSecret").Trim()).ConfigureAwait(true);
            case "discord.disconnect":
                _settings.Update(s => s.DiscordTokensProtected = null);
                _coordinator.Discord.Reconfigure();
                return null;
            case "app.openExternal":
                string url = Str(args, "url");
                if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Lien refusé.");
                AppPaths.Open(url);
                return null;
            case "app.openOutputFolder":
                Directory.CreateDirectory(_settings.Current.OutputFolder);
                AppPaths.Open(_settings.Current.OutputFolder);
                return null;
            case "app.openLogs":
                AppPaths.Open(AppPaths.LogsFolder);
                return null;
        }
        throw new InvalidOperationException($"Action inconnue : {type}");
    }

    public object BuildState()
    {
        var status = _coordinator.Status;
        var settings = _settings.Current;
        var (me, others) = _coordinator.Levels;
        var discord = _coordinator.Discord.State;
        return new
        {
            state = status.State.ToString().ToLowerInvariant(),
            selectedApp = AppId(settings.SelectedApp),
            autoRecord = settings.AutoRecord,
            autoStop = settings.AutoStopAtCallEnd,
            theme = settings.Theme,
            summaryReady = settings.SummaryProvider != SummaryProvider.Claude || !string.IsNullOrEmpty(_settings.ClaudeApiKey),
            apps = status.Apps.Select(a => new { id = AppId(a.App), name = CallApps.Name(a.App), running = a.Running, inCall = a.InCall }),
            recording = status.RecordingApp is { } app
                ? new
                {
                    app = AppId(app),
                    elapsed = status.Elapsed.TotalSeconds,
                    manual = status.Manual,
                    muted = status.Mute?.Muted ?? true,
                    muteSource = status.Mute?.Source switch
                    {
                        MuteSource.TeamsApi => "api",
                        MuteSource.AppWindow => "window",
                        MuteSource.DiscordRpc => "discord",
                        _ => "unknown",
                    },
                    pushToTalk = status.Mute?.Source == MuteSource.DiscordRpc && discord.PushToTalk,
                    levels = new { me, others },
                    speakers = _coordinator.Speakers is { } live
                        ? new
                        {
                            meSpeaking = live.MeSpeaking,
                            meTalk = live.MeTalkSeconds,
                            current = live.CurrentSpeakerId,
                            channel = live.Channel,
                            exact = live.ExactNames,
                            list = live.Speakers.Select(sp => new { id = sp.Id, name = sp.Name, speaking = sp.Speaking, talk = sp.TalkSeconds, muted = sp.Muted, present = sp.Present }),
                        }
                        : null,
                }
                : null,
            processing = status.ProcessingQueueLength > 0
                ? new { text = status.ProcessingText, queue = status.ProcessingQueueLength }
                : null,
            teams = new { connected = status.Api.Connected, paired = status.ApiPaired, inMeeting = status.Api.HasMeetingState && status.Api.IsInMeeting },
            discord = new
            {
                status = discord.Status switch
                {
                    DiscordLinkStatus.NotConfigured => "notConfigured",
                    DiscordLinkStatus.DiscordClosed => "closed",
                    DiscordLinkStatus.NeedsAuthorization => "needsAuth",
                    DiscordLinkStatus.Connecting => "connecting",
                    DiscordLinkStatus.Connected => "connected",
                    _ => "error",
                },
                error = discord.Error,
                user = discord.Me?.DisplayName,
                inVoice = discord.InVoice,
                channel = DiscordCallLog.ChannelLabel(discord.ChannelName, discord.GuildName),
                participants = discord.Participants.Count,
            },
        };
    }

    /// <summary>
    /// Saves the user's Discord application, then asks Discord to show its authorization
    /// window (the call waits until the user answers in Discord).
    /// </summary>
    private async Task<object> ConnectDiscordAsync(string clientId, string clientSecret)
    {
        if (!Regex.IsMatch(clientId, @"^\d{15,22}$"))
        {
            throw new InvalidOperationException("Client ID invalide : c'est une suite de chiffres (portail développeur Discord → votre application → OAuth2 → Client ID).");
        }
        var current = _settings.Current;
        if (clientSecret.Length == 0 && string.IsNullOrEmpty(current.DiscordClientSecretProtected))
        {
            throw new InvalidOperationException("Collez le Client Secret de votre application Discord (OAuth2 → Reset Secret).");
        }

        _settings.Update(s =>
        {
            if (s.DiscordClientId != clientId) s.DiscordTokensProtected = null;
            s.DiscordClientId = clientId;
            if (clientSecret.Length > 0) _settings.SetDiscordClientSecret(s, clientSecret);
        });
        _coordinator.Discord.Reconfigure();
        var me = await _coordinator.Discord.AuthorizeAsync(CancellationToken.None).ConfigureAwait(true);
        return new { user = me.DisplayName };
    }

    private object ToDto(RecordingSummary r)
    {
        string? current = _coordinator.Processing.CurrentFolderPath;
        bool processingNow = current is not null && Path.GetFileName(current) == r.Id;
        return new
        {
            id = r.Id,
            title = r.Title,
            app = r.App,
            startedAt = r.StartedAt,
            duration = r.DurationSeconds,
            status = processingNow ? "processing" : r.Status.ToString().ToLowerInvariant(),
            progress = processingNow ? _coordinator.Processing.CurrentProgress : null,
            error = r.Error,
            hasSummary = r.HasSummary,
            audioBytes = r.AudioBytes,
        };
    }

    private object BuildDetail(RecordingLibrary library, string id)
    {
        var detail = library.Get(id) ?? throw new InvalidOperationException("Enregistrement introuvable.");
        var settings = _settings.Current;
        var naming = new SpeakerNaming(string.IsNullOrWhiteSpace(detail.MyName) ? settings.MyName : detail.MyName!, settings.OthersName, detail.SpeakerNames);
        var voices = detail.Paragraphs
            .Where(p => p.Speaker == Speaker.Others && p.Voice > 0)
            .GroupBy(p => p.Voice)
            .Select(g => new { voice = g.Key, name = naming.NameOf(Speaker.Others, g.Key), talk = g.Sum(p => (p.End - p.Start).TotalSeconds) })
            .OrderByDescending(v => v.talk)
            .ToList();
        return new
        {
            summary = ToDto(detail.Summary),
            summaryHtml = detail.SummaryMarkdown is { } md ? ReportWriter.MarkdownToHtml(md) : null,
            voices,
            meTalk = detail.Paragraphs.Where(p => p.Speaker == Speaker.Me).Sum(p => (p.End - p.Start).TotalSeconds),
            paragraphs = detail.Paragraphs.Select(p => new
            {
                start = p.Start.TotalSeconds,
                end = p.End.TotalSeconds,
                speaker = p.Speaker == Speaker.Me ? "me" : "others",
                voice = p.Voice,
                name = naming.NameOf(p.Speaker, p.Voice),
                text = p.Text,
            }),
            audioUrl = detail.AudioRelativePath is { } audio ? $"https://{MediaHost}/{audio}" : null,
            hasReport = File.Exists(Path.Combine(detail.FolderPath, CallFolder.ReportFileName)),
            myName = naming.MyName,
            othersName = settings.OthersName,
            exactNames = detail.ExactNames,
        };
    }

    private object BuildSettings()
    {
        var s = _settings.Current;
        return new
        {
            theme = s.Theme,
            myName = s.MyName,
            othersName = s.OthersName,
            outputFolder = s.OutputFolder,
            startWithWindows = AutoStart.IsEnabled,
            notifyOnStart = s.NotifyOnCallStart,
            opusBitrate = s.OpusBitrate,
            microphoneId = s.MicrophoneDeviceId,
            echoCancellation = s.EchoCancellation,
            recordMicWhenUnknown = s.RecordMicWhenMuteUnknown,
            whisperModel = s.WhisperModel,
            language = s.Language,
            useGpu = s.UseGpu,
            identifyVoices = s.IdentifyVoices,
            vocabulary = s.Vocabulary,
            summaryProvider = s.SummaryProvider.ToString().ToLowerInvariant(),
            hasApiKey = !string.IsNullOrEmpty(s.ClaudeApiKeyProtected),
            claudeModel = s.ClaudeModel,
            ollamaUrl = s.OllamaUrl,
            ollamaModel = s.OllamaModel,
            discordClientId = s.DiscordClientId,
            hasDiscordSecret = !string.IsNullOrEmpty(s.DiscordClientSecretProtected),
        };
    }

    private static object BuildOptions()
    {
        var microphones = new List<object>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                microphones.Add(new { id = device.ID, name = device.FriendlyName });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Liste des micros indisponible : {ex.Message}");
        }

        var folders = ModelCatalog.SearchFolders(AppPaths.AppFolder, AppPaths.DataFolder).ToList();
        return new
        {
            microphones,
            models = ModelCatalog.Models.Select(m => new
            {
                file = m.FileName,
                label = m.Label,
                sizeMb = m.SizeMb,
                installed = folders.Any(f => File.Exists(Path.Combine(f, m.FileName))),
            }),
            languages = Languages.Select(l => new { code = l.Code, name = l.Name }),
        };
    }

    private void SaveSettings(JsonElement args)
    {
        string Text(string name, string fallback)
        {
            string value = Str(args, name).Trim();
            return value.Length == 0 ? fallback : value;
        }
        bool Flag(string name) => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        string oldFolder = _settings.Current.OutputFolder;
        string apiKey = Str(args, "apiKey");
        _settings.Update(s =>
        {
            s.Theme = Text("theme", "dark");
            s.MyName = Text("myName", "Moi");
            s.OthersName = Text("othersName", "Participants");
            s.OutputFolder = Text("outputFolder", AppSettings.DefaultOutputFolder);
            s.NotifyOnCallStart = Flag("notifyOnStart");
            s.OpusBitrate = args.TryGetProperty("opusBitrate", out var b) && b.TryGetInt32(out int bitrate) ? Math.Clamp(bitrate, 12000, 64000) : 24000;
            s.MicrophoneDeviceId = Str(args, "microphoneId");
            s.EchoCancellation = Flag("echoCancellation");
            s.RecordMicWhenMuteUnknown = Flag("recordMicWhenUnknown");
            s.WhisperModel = Str(args, "whisperModel");
            s.Language = Text("language", "fr");
            s.UseGpu = Flag("useGpu");
            s.IdentifyVoices = Flag("identifyVoices");
            s.Vocabulary = Str(args, "vocabulary").Trim();
            s.SummaryProvider = Str(args, "summaryProvider") switch
            {
                "ollama" => SummaryProvider.Ollama,
                "none" => SummaryProvider.None,
                _ => SummaryProvider.Claude,
            };
            if (apiKey.Trim().Length > 0) _settings.SetClaudeApiKey(s, apiKey);
            s.ClaudeModel = Text("claudeModel", "claude-opus-5-5");
            s.OllamaUrl = Text("ollamaUrl", "http://localhost:11434");
            s.OllamaModel = Text("ollamaModel", "mistral-nemo");
        });

        try
        {
            AutoStart.Set(Flag("startWithWindows"));
        }
        catch (Exception ex)
        {
            Log.Warn($"Démarrage avec Windows non modifié : {ex.Message}");
        }

        ThemeChanged?.Invoke(_settings.Current.Theme);
        if (!string.Equals(oldFolder, _settings.Current.OutputFolder, StringComparison.OrdinalIgnoreCase))
        {
            OutputFolderChanged?.Invoke(_settings.Current.OutputFolder);
        }
    }

    private void EnsureNotBusy(CallFolder folder)
    {
        string? processing = _coordinator.Processing.CurrentFolderPath;
        if (folder.ReadMetadata().Status == CallStatus.Recording || string.Equals(processing, folder.Path, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cet appel est en cours d'enregistrement ou de traitement.");
        }
    }

    private static CallFolder Require(RecordingLibrary library, string id) =>
        library.Find(id) ?? throw new InvalidOperationException("Enregistrement introuvable.");

    private static string Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    public static string AppId(CallApp app) => app.ToString().ToLowerInvariant();

    private static CallApp ParseApp(string id) => id switch
    {
        "discord" => CallApp.Discord,
        "teams" => CallApp.Teams,
        _ => throw new InvalidOperationException($"Application inconnue : {id}"),
    };
}
