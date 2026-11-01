using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Echo.Core.Audio;
using Echo.Core.Discord;
using Echo.Core.Output;
using Echo.Core.Processing;
using Echo.Core.Settings;
using Echo.Core.Speakers;
using Echo.Core.Teams;
using Echo.Core.Transcription;

namespace Echo.Tests;

/// <summary>
/// A stand-in for the Discord desktop app: a local pipe speaking the Discord IPC protocol,
/// with a voice channel (me + two friends).
/// </summary>
internal sealed class FakeDiscord : IAsyncDisposable
{
    public const string MyId = "100";
    public const string ValidToken = "access-1";

    private readonly string _pipeName = $"echo-test-discord-{Guid.NewGuid():N}";
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _servers = [];
    private DiscordIpcChannel? _channel;

    public List<string> Commands { get; } = [];

    public List<string> Subscriptions { get; } = [];

    public string? HandshakeClientId { get; private set; }

    public string? AuthenticatedWith { get; private set; }

    /// <summary>Access tokens AUTHENTICATE accepts.</summary>
    public HashSet<string> AcceptedTokens { get; } = [ValidToken];

    public bool RejectClientId { get; set; }

    public bool InChannel { get; set; } = true;

    public int Connections { get; private set; }

    /// <summary>Connector for <see cref="DiscordVoiceSession"/>: accepts one more connection each time.</summary>
    public async Task<Stream?> ConnectAsync(CancellationToken cancellationToken)
    {
        var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync(_cts.Token);
        var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, cancellationToken);
        await accepted;
        Connections++;
        _servers.Add(Task.Run(() => ServeAsync(server)));
        return client;
    }

    private async Task ServeAsync(NamedPipeServerStream stream)
    {
        await using var channel = new DiscordIpcChannel(stream);
        _channel = channel;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await channel.ReadAsync(_cts.Token);
                if (frame is not { } f) return;
                var message = JsonNode.Parse(f.Json)!;
                if (f.Opcode == DiscordOpcode.Handshake)
                {
                    HandshakeClientId = (string?)message["client_id"];
                    if (RejectClientId)
                    {
                        await channel.WriteAsync(DiscordOpcode.Close, """{"code":4000,"message":"Invalid Client ID"}""", _cts.Token);
                        return;
                    }
                    await Dispatch("READY", new JsonObject { ["v"] = 1, ["user"] = User(MyId, "emerick", "Emerick") });
                    continue;
                }

                string cmd = (string)message["cmd"]!;
                string nonce = (string)message["nonce"]!;
                var args = message["args"] as JsonObject;
                lock (Commands) Commands.Add(cmd);
                var (data, error) = Answer(cmd, args, (string?)message["evt"]);
                var reply = new JsonObject { ["cmd"] = cmd, ["nonce"] = nonce, ["data"] = data, ["evt"] = error ? "ERROR" : null };
                await channel.WriteAsync(DiscordOpcode.Frame, reply.ToJsonString(), _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    private (JsonNode? Data, bool Error) Answer(string cmd, JsonObject? args, string? evt)
    {
        switch (cmd)
        {
            case "AUTHORIZE":
                return (new JsonObject { ["code"] = "the-code" }, false);
            case "AUTHENTICATE":
                string token = (string)args!["access_token"]!;
                if (!AcceptedTokens.Contains(token)) return (new JsonObject { ["code"] = 4009, ["message"] = "Invalid access token" }, true);
                AuthenticatedWith = token;
                return (new JsonObject { ["user"] = User(MyId, "emerick", "Emerick"), ["scopes"] = new JsonArray("rpc", "rpc.voice.read") }, false);
            case "SUBSCRIBE":
                lock (Subscriptions) Subscriptions.Add(evt + (args?["channel_id"] is { } c ? $":{c}" : string.Empty));
                return (new JsonObject { ["evt"] = evt }, false);
            case "UNSUBSCRIBE":
                return (new JsonObject { ["evt"] = evt }, false);
            case "GET_VOICE_SETTINGS":
                return (new JsonObject { ["mute"] = false, ["deaf"] = false, ["mode"] = new JsonObject { ["type"] = "VOICE_ACTIVITY" } }, false);
            case "GET_SELECTED_VOICE_CHANNEL":
                return (InChannel ? Channel() : null, false);
            case "GET_CHANNEL":
                return (Channel(), false);
            case "GET_GUILD":
                return (new JsonObject { ["id"] = "9", ["name"] = "Les Copains" }, false);
        }
        return (new JsonObject { ["code"] = 4002, ["message"] = "Invalid command" }, true);
    }

    private static JsonObject Channel() => new()
    {
        ["id"] = "500",
        ["name"] = "Raid",
        ["guild_id"] = "9",
        ["type"] = 2,
        ["voice_states"] = new JsonArray(
            VoiceState(MyId, "emerick", "Emerick", nick: null),
            VoiceState("200", "thomas", "Thomas", nick: "Thomas_92"),
            VoiceState("300", "sarah.b", null, nick: null, selfMute: true)),
    };

    public static JsonObject VoiceState(string id, string username, string? globalName, string? nick, bool selfMute = false) => new()
    {
        ["nick"] = nick,
        ["mute"] = false,
        ["volume"] = 100,
        ["user"] = User(id, username, globalName),
        ["voice_state"] = new JsonObject { ["mute"] = false, ["deaf"] = false, ["self_mute"] = selfMute, ["self_deaf"] = false, ["suppress"] = false },
    };

    private static JsonObject User(string id, string username, string? globalName) =>
        new() { ["id"] = id, ["username"] = username, ["global_name"] = globalName, ["avatar"] = null };

    /// <summary>Pushes an event to the connected client.</summary>
    public Task Dispatch(string evt, JsonNode data) =>
        _channel!.WriteAsync(DiscordOpcode.Frame, new JsonObject { ["cmd"] = "DISPATCH", ["evt"] = evt, ["data"] = data, ["nonce"] = null }.ToJsonString(), _cts.Token);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var server in _servers)
        {
            try
            {
                await server;
            }
            catch (Exception)
            {
            }
        }
        _cts.Dispose();
    }
}

/// <summary>Plays discord.com/api/oauth2/token.</summary>
internal sealed class FakeTokenEndpoint : HttpMessageHandler
{
    public List<Dictionary<string, string>> Requests { get; } = [];

    public string NextAccessToken { get; set; } = FakeDiscord.ValidToken;

    public string? RefuseWith { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal(DiscordOAuth.TokenUrl, request.RequestUri!.ToString());
        string body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var form = body.Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
        lock (Requests) Requests.Add(form);
        if (RefuseWith is not null)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent($$"""{"error":"{{RefuseWith}}","error_description":"refused"}""", Encoding.UTF8, "application/json"),
            };
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"access_token":"{{NextAccessToken}}","refresh_token":"refresh-2","expires_in":604800,"token_type":"Bearer"}""", Encoding.UTF8, "application/json"),
        };
    }
}

public class DiscordSessionTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public FakeDiscord Discord { get; } = new();

        public FakeTokenEndpoint Http { get; } = new();

        public DiscordTokens? Tokens { get; set; }

        public string ClientId { get; set; } = "1301234567890123456";

        public List<(string User, bool Speaking)> Speaking { get; } = [];

        public DiscordVoiceSession Session { get; }

        private readonly CancellationTokenSource _cts = new();
        private readonly Task _run;
        private readonly HttpClient _client;

        public Harness(DiscordTokens? tokens = null, bool start = true)
        {
            Tokens = tokens;
            _client = new HttpClient(Http);
            Session = new DiscordVoiceSession(
                () => new DiscordCredentials(ClientId, "secret", Tokens),
                t => Tokens = t,
                Discord.ConnectAsync,
                new DiscordOAuth(_client),
                retryDelay: TimeSpan.FromMilliseconds(100));
            Session.SpeakingChanged += (user, speaking, _) =>
            {
                lock (Speaking) Speaking.Add((user, speaking));
            };
            _run = start ? Task.Run(() => Session.RunAsync(_cts.Token)) : Task.CompletedTask;
        }

        public async Task<DiscordVoiceState> WaitFor(Func<DiscordVoiceState, bool> condition, string what)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var state = Session.State;
                if (condition(state)) return state;
                await Task.Delay(20);
            }
            throw new TimeoutException($"Attendu : {what} (état : {Session.State.Status} {Session.State.Error})");
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await _run;
            await Discord.DisposeAsync();
            _client.Dispose();
        }
    }

    [Fact]
    public async Task Authorizing_gives_exact_names_speaking_and_microphone_state()
    {
        await using var h = new Harness();
        var waiting = await h.WaitFor(s => s.Status == DiscordLinkStatus.NeedsAuthorization, "autorisation demandée");
        Assert.Equal("1301234567890123456", h.Discord.HandshakeClientId);
        Assert.Equal("Emerick", waiting.Me?.DisplayName);     // the logged-in account, known before authorizing

        var me = await h.Session.AuthorizeAsync(CancellationToken.None);
        Assert.Equal("Emerick", me.DisplayName);

        // Code exchanged for tokens with the user's application, then saved.
        var exchange = Assert.Single(h.Http.Requests);
        Assert.Equal("authorization_code", exchange["grant_type"]);
        Assert.Equal("the-code", exchange["code"]);
        Assert.Equal("1301234567890123456", exchange["client_id"]);
        Assert.Equal("secret", exchange["client_secret"]);
        Assert.Equal(DiscordOAuth.RedirectUri, exchange["redirect_uri"]);
        Assert.Equal(FakeDiscord.ValidToken, h.Tokens?.AccessToken);
        Assert.Equal("refresh-2", h.Tokens?.RefreshToken);
        Assert.Equal(FakeDiscord.ValidToken, h.Discord.AuthenticatedWith);

        // In the voice channel: everyone but me, with the name Discord shows.
        var state = await h.WaitFor(s => s.InVoice && s.Participants.Count == 2, "salon vocal");
        Assert.Equal("Raid", state.ChannelName);
        Assert.Equal("Les Copains", state.GuildName);
        Assert.Equal(["Thomas_92", "sarah.b"], state.Participants.Select(p => p.Name));
        Assert.True(state.Participants[1].Muted);
        Assert.False(state.MicrophoneMuted);
        Assert.Contains("SPEAKING_START:500", h.Discord.Subscriptions);
        Assert.Contains("VOICE_STATE_UPDATE:500", h.Discord.Subscriptions);
        Assert.Contains("VOICE_CHANNEL_SELECT", h.Discord.Subscriptions);

        // Who speaks, as Discord shows it.
        await h.Discord.Dispatch("SPEAKING_START", new JsonObject { ["user_id"] = "200", ["channel_id"] = "500" });
        state = await h.WaitFor(s => s.Participants.Any(p => p.Speaking), "Thomas parle");
        Assert.True(state.Participants[0].Speaking);
        lock (h.Speaking) Assert.Contains(("200", true), h.Speaking);

        // Muted in Discord.
        await h.Discord.Dispatch("VOICE_SETTINGS_UPDATE", new JsonObject { ["mute"] = true, ["deaf"] = false, ["mode"] = new JsonObject { ["type"] = "VOICE_ACTIVITY" } });
        await h.WaitFor(s => s.MicrophoneMuted == true, "micro coupé");

        // Push-to-talk: only transmitted while the key is down (Discord shows me speaking).
        await h.Discord.Dispatch("VOICE_SETTINGS_UPDATE", new JsonObject { ["mute"] = false, ["deaf"] = false, ["mode"] = new JsonObject { ["type"] = "PUSH_TO_TALK" } });
        state = await h.WaitFor(s => s.PushToTalk, "push-to-talk");
        Assert.True(state.MicrophoneMuted);
        await h.Discord.Dispatch("SPEAKING_START", new JsonObject { ["user_id"] = FakeDiscord.MyId, ["channel_id"] = "500" });
        await h.WaitFor(s => s.MicrophoneMuted == false && s.MeSpeaking, "touche enfoncée");
        await h.Discord.Dispatch("SPEAKING_STOP", new JsonObject { ["user_id"] = FakeDiscord.MyId, ["channel_id"] = "500" });
        await h.WaitFor(s => s.MicrophoneMuted == true, "touche relâchée");

        // Someone joins with a server nickname.
        await h.Discord.Dispatch("VOICE_STATE_CREATE", FakeDiscord.VoiceState("400", "lucas", "Lucas", nick: "Lulu"));
        state = await h.WaitFor(s => s.Participants.Count == 3, "Lulu arrive");
        Assert.Equal("Lulu", state.Participants[2].Name);

        // Leaving the call: no channel any more, and whoever was speaking stops.
        await h.Discord.Dispatch("VOICE_CHANNEL_SELECT", new JsonObject { ["channel_id"] = null, ["guild_id"] = null });
        state = await h.WaitFor(s => !s.InVoice, "appel quitté");
        Assert.Empty(state.Participants);
        Assert.Null(state.MicrophoneMuted);
        lock (h.Speaking) Assert.Contains(("200", false), h.Speaking);

        // Joining another channel.
        await h.Discord.Dispatch("VOICE_CHANNEL_SELECT", new JsonObject { ["channel_id"] = "500", ["guild_id"] = "9" });
        await h.WaitFor(s => s.InVoice && s.Participants.Count == 2, "nouvel appel");
    }

    [Fact]
    public async Task Saved_tokens_connect_directly_and_expired_ones_are_renewed()
    {
        var expired = new DiscordTokens("old", "refresh-1", DateTimeOffset.UtcNow.AddHours(-1));
        await using var h = new Harness(expired);
        await h.WaitFor(s => s.InVoice, "connecté");

        var refresh = Assert.Single(h.Http.Requests);
        Assert.Equal("refresh_token", refresh["grant_type"]);
        Assert.Equal("refresh-1", refresh["refresh_token"]);
        Assert.Equal(FakeDiscord.ValidToken, h.Tokens?.AccessToken);
        Assert.DoesNotContain("AUTHORIZE", h.Discord.Commands);
    }

    [Fact]
    public async Task A_token_Discord_rejects_is_renewed_once()
    {
        var revoked = new DiscordTokens("revoked", "refresh-1", DateTimeOffset.UtcNow.AddDays(5));
        await using var h = new Harness(revoked);
        await h.WaitFor(s => s.Connected, "connecté");
        Assert.Equal(FakeDiscord.ValidToken, h.Discord.AuthenticatedWith);
        Assert.Equal("refresh_token", Assert.Single(h.Http.Requests)["grant_type"]);
    }

    [Fact]
    public async Task Refused_renewal_asks_for_a_new_authorization()
    {
        var expired = new DiscordTokens("old", "refresh-1", DateTimeOffset.UtcNow.AddHours(-1));
        await using var h = new Harness(expired);
        h.Http.RefuseWith = "invalid_grant";
        var state = await h.WaitFor(s => s.Status == DiscordLinkStatus.NeedsAuthorization && s.Error is not null, "autorisation à refaire");
        Assert.Null(h.Tokens);
        Assert.Contains("reconnectez", state.Error);
    }

    [Fact]
    public async Task Wrong_client_id_is_reported()
    {
        await using var h = new Harness(start: false);
        h.Discord.RejectClientId = true;
        await using var _ = new AsyncRun(h.Session);
        var state = await h.WaitFor(s => s.Status == DiscordLinkStatus.Error, "erreur");
        Assert.Contains("Client ID", state.Error);
    }

    [Fact]
    public async Task Discord_closed_then_opened()
    {
        bool running = false;
        var discord = new FakeDiscord();
        await using var _d = discord;
        using var http = new HttpClient(new FakeTokenEndpoint());
        var session = new DiscordVoiceSession(
            () => new DiscordCredentials("1301234567890123456", "secret", new DiscordTokens(FakeDiscord.ValidToken, "r", DateTimeOffset.UtcNow.AddDays(6))),
            _ => { },
            ct => running ? discord.ConnectAsync(ct) : Task.FromResult<Stream?>(null),
            new DiscordOAuth(http),
            retryDelay: TimeSpan.FromMilliseconds(50));
        await using var run = new AsyncRun(session);

        await Until(() => session.State.Status == DiscordLinkStatus.DiscordClosed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AuthorizeAsync(CancellationToken.None));
        running = true;
        await Until(() => session.State.InVoice);
    }

    [Fact]
    public async Task Not_configured_until_an_application_is_entered()
    {
        DiscordCredentials? credentials = null;
        var discord = new FakeDiscord();
        await using var _d = discord;
        using var http = new HttpClient(new FakeTokenEndpoint());
        var session = new DiscordVoiceSession(() => credentials, _ => { }, discord.ConnectAsync, new DiscordOAuth(http));
        await using var run = new AsyncRun(session);

        await Until(() => session.State.Status == DiscordLinkStatus.NotConfigured);
        await Task.Delay(100);
        Assert.Equal(0, discord.Connections);

        credentials = new DiscordCredentials("1301234567890123456", "secret", null);
        session.Reconfigure();
        await Until(() => session.State.Status == DiscordLinkStatus.NeedsAuthorization);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    private sealed class AsyncRun : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _task;

        public AsyncRun(DiscordVoiceSession session) => _task = Task.Run(() => session.RunAsync(_cts.Token));

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await _task;
            _cts.Dispose();
        }
    }
}

public class DiscordProtocolTests
{
    [Fact]
    public async Task Frames_round_trip_with_little_endian_header()
    {
        var buffer = new MemoryStream();
        var writer = new DiscordIpcChannel(buffer);
        await writer.WriteAsync(DiscordOpcode.Frame, """{"cmd":"Ça va"}""", CancellationToken.None);
        byte[] bytes = buffer.ToArray();
        Assert.Equal([1, 0, 0, 0], bytes[..4]);
        Assert.Equal(Encoding.UTF8.GetByteCount("""{"cmd":"Ça va"}"""), BitConverter.ToInt32(bytes, 4));

        var reader = new DiscordIpcChannel(new MemoryStream(bytes));
        var frame = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(DiscordOpcode.Frame, frame?.Opcode);
        Assert.Equal("""{"cmd":"Ça va"}""", frame?.Json);
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public void Tokens_survive_a_round_trip_and_are_renewed_a_day_ahead()
    {
        var tokens = new DiscordTokens("a", "r", DateTimeOffset.UtcNow.AddDays(3));
        Assert.Equal(tokens, DiscordTokens.FromJson(tokens.ToJson()));
        Assert.False(tokens.NeedsRefresh(DateTimeOffset.UtcNow));
        Assert.True(tokens.NeedsRefresh(DateTimeOffset.UtcNow.AddDays(2.5)));
        Assert.Null(DiscordTokens.FromJson("nope"));
    }

    [Fact]
    public async Task Wrong_secret_gives_a_clear_message()
    {
        using var http = new HttpClient(new FakeTokenEndpoint { RefuseWith = "invalid_client" });
        var ex = await Assert.ThrowsAsync<DiscordAuthException>(() => new DiscordOAuth(http).ExchangeCodeAsync("1", "bad", "code", CancellationToken.None));
        Assert.Contains("Client Secret", ex.Message);
    }

    [Fact]
    public void Discord_state_drives_mute_and_call_detection()
    {
        var api = TeamsApiState.Disconnected;
        Assert.Equal(new MuteDecision(true, MuteSource.DiscordRpc), MuteResolver.Resolve(api, false, false, true, discordMuted: true));
        Assert.Equal(new MuteDecision(false, MuteSource.DiscordRpc), MuteResolver.Resolve(api, false, true, false, discordMuted: false));
        Assert.Equal(MuteSource.AppWindow, MuteResolver.Resolve(api, false, true, false, discordMuted: null).Source);

        var detector = new CallDetector(micOnlyStartDelay: TimeSpan.FromSeconds(2));
        var now = new DateTime(2026, 11, 1, 20, 0, 0);
        detector.SetAppRunning(true);
        detector.SetMicInUse(true);
        detector.SetDirectCallState(false);              // Discord says: not in a voice channel
        Assert.Equal(CallEvent.None, detector.Tick(now));
        Assert.False(detector.CallInProgress);
        detector.SetDirectCallState(true);               // joined: starts at once
        Assert.Equal(CallEvent.Started, detector.Tick(now.AddSeconds(1)));
    }
}

public class DiscordCallLogTests
{
    private const long Second = 10_000_000;

    private static DiscordVoiceState InCall(params DiscordParticipant[] participants) => DiscordVoiceState.Initial with
    {
        Status = DiscordLinkStatus.Connected,
        Me = new DiscordUserInfo("100", "emerick", "Emerick"),
        ChannelId = "500",
        ChannelName = "Raid",
        GuildName = "Les Copains",
        Participants = participants,
    };

    [Fact]
    public void Speaking_turns_are_placed_on_the_recording_timeline()
    {
        long start = 1_000 * Second;
        var thomas = new DiscordParticipant("200", "Thomas_92", "thomas", false, false, false);
        var sarah = new DiscordParticipant("300", "Sarah", "sarah", true, false, false);
        var log = new DiscordCallLog(start, InCall(thomas, sarah), start + 2 * Second);

        log.Speaking("200", true, start + 3 * Second);
        log.Speaking("300", false, start + 4 * Second);
        log.Speaking("100", true, start + 5 * Second);

        var live = log.Snapshot(start + 6 * Second);
        Assert.True(live.ExactNames);
        Assert.Equal("Raid · Les Copains", live.Channel);
        Assert.True(live.MeSpeaking);
        Assert.Equal(1, live.MeTalkSeconds, 3);
        Assert.Equal(["Thomas_92", "Sarah"], live.Speakers.Select(s => s.Name));
        Assert.Equal(1, live.CurrentSpeakerId);
        Assert.Equal(3, live.Speakers[0].TalkSeconds, 3);
        Assert.Equal(2, live.Speakers[1].TalkSeconds, 3);

        log.Speaking("100", false, start + 7 * Second);

        // Sarah leaves; Lucas joins and is renamed in Discord.
        log.Update(InCall(thomas with { Speaking = true }, new DiscordParticipant("400", "Lulu", "lucas", false, true, false)));
        live = log.Snapshot(start + 8 * Second);
        Assert.False(live.Speakers.Single(s => s.Name == "Sarah").Present);
        Assert.True(live.Speakers.Single(s => s.Name == "Lulu").Muted);

        var data = log.Finish(start + 10 * Second);
        Assert.Equal("Emerick", data.Me?.Name);
        Assert.Equal(0, data.Me?.Voice);
        Assert.Equal([[5.0, 7.0]], data.Me!.Speaking);
        Assert.Equal([1, 2, 3], data.Participants.Select(p => p.Voice));
        Assert.Equal([[3.0, 10.0]], data.Participants[0].Speaking);
        Assert.Equal([[2.0, 4.0]], data.Participants[1].Speaking);
        Assert.Empty(data.Participants[2].Speaking);

        string path = Path.Combine(TestPaths.TempDir(), DiscordCallData.FileName);
        data.Save(path);
        var loaded = DiscordCallData.Load(path)!;
        Assert.Equal("Thomas_92", loaded.Participants[0].Name);
        Assert.Equal(7, loaded.Participants[0].TalkSeconds, 3);
        Assert.True(loaded.HasSpeech);
    }

    [Fact]
    public void Sentences_take_the_name_of_whoever_Discord_showed_speaking()
    {
        var data = new DiscordCallData
        {
            Participants =
            [
                new DiscordCallMember { Voice = 1, Id = "200", Name = "Thomas_92", Speaking = [[1.0, 4.2], [20.0, 22.0]] },
                new DiscordCallMember { Voice = 2, Id = "300", Name = "Sarah", Speaking = [[6.1, 9.0]] },
                new DiscordCallMember { Voice = 3, Id = "400", Name = "Lulu" },
            ],
        };
        TranscriptSegment Seg(double a, double b, Speaker who = Speaker.Others) => new(TimeSpan.FromSeconds(a), TimeSpan.FromSeconds(b), "x", who);

        var (segments, names) = DiscordAttribution.Apply(
            [Seg(1.2, 4.0), Seg(4.0, 7.0), Seg(6.5, 9.4), Seg(11, 12, Speaker.Me), Seg(10.0, 10.5), Seg(15, 16)],
            data);

        Assert.Equal([1, 2, 2, 0, 2, 0], segments.Select(s => s.Voice));   // mostly Sarah; 1 s after her turn; nobody near 15 s
        Assert.Equal(Speaker.Me, segments[3].Speaker);
        Assert.Equal(new Dictionary<int, string> { [1] = "Thomas_92", [2] = "Sarah" }, names);
    }

    [Fact]
    public async Task Transcript_uses_the_Discord_names_instead_of_voice_prints()
    {
        string root = TestPaths.TempDir();
        var folder = CallFolder.Create(root, new DateTime(2026, 11, 1, 21, 0, 0));
        folder.WriteMetadata(new CallMetadata { App = "Discord", Title = "Appel Discord", StartedAt = new DateTime(2026, 11, 1, 21, 0, 0), Status = CallStatus.Pending });

        using (var opus = new OpusRecordingWriter(folder.AudioPath, 32000, "Test"))
        using (var envelope = new EnvelopeWriter(folder.EnvelopePath))
        {
            var mixer = new CallMixer(0, new MuteGate(false), [opus, envelope], TimeSpan.Zero);
            var remote = mixer.AddTrack("app", TrackRole.Remote);
            var me = mixer.AddTrack("mic", TrackRole.Me);
            remote.Write(CodecTests.Sine(300, 0.3f, 48000 * 3), 48000 * 1);
            remote.Write(CodecTests.Sine(1100, 0.3f, 48000 * 3), 48000 * 6);
            me.Write(CodecTests.Sine(700, 0.3f, 48000 * 2), 48000 * 11);
            remote.Write(CodecTests.Sine(300, 0.3f, 48000 * 3), 48000 * 15);
            remote.PadTo(48000 * 20);
            mixer.Flush();
        }
        new DiscordCallData
        {
            Me = new DiscordCallMember { Voice = 0, Id = "100", Name = "Emerick", Speaking = [[11.0, 13.1]] },
            Channel = "Raid",
            Participants =
            [
                new DiscordCallMember { Voice = 1, Id = "200", Name = "Thomas_92", Speaking = [[0.9, 4.2], [15.1, 18.3]] },
                new DiscordCallMember { Voice = 2, Id = "300", Name = "Sarah ✦", Speaking = [[6.0, 9.1]] },
            ],
        }.Save(folder.DiscordPath);

        var embedder = new ToneEmbedder();
        var processor = new CallProcessor(() => new VoicePipelineTests.SegmentPerRegionTranscriber(), () => null, () => embedder);
        var result = await processor.ProcessAsync(folder, new ProcessingOptions(AppSettings.DefaultMyName, "Participants", "fake", null), null, CancellationToken.None);

        Assert.Equal(0, embedder.Calls);                 // no voice prints needed
        Assert.Equal("discord", result.Metadata.SpeakerSource);
        Assert.Equal("Emerick", result.Metadata.MyName);
        string text = File.ReadAllText(folder.TranscriptPath);
        Assert.Contains("] Thomas_92 : phrase 1", text);
        Assert.Contains("] Sarah ✦ : phrase 2", text);
        Assert.Contains("] Emerick : phrase 3", text);
        Assert.Contains("] Thomas_92 : phrase 4", text);
        Assert.DoesNotContain("Intervenant", text);

        // Renaming still works, and keeps the Discord name of the user.
        Assert.True(CallOutputs.RenameVoice(folder, 2, "Sarah B.", AppSettings.DefaultMyName, "Participants"));
        text = File.ReadAllText(folder.TranscriptPath);
        Assert.Contains("] Sarah B. : phrase 2", text);
        Assert.Contains("] Emerick : phrase 3", text);

        var detail = new RecordingLibrary(root).Get(Path.GetFileName(folder.Path))!;
        Assert.True(detail.ExactNames);
        Assert.Equal("Emerick", detail.MyName);
    }

    [Fact]
    public async Task A_custom_name_in_the_settings_wins_over_the_Discord_name()
    {
        string root = TestPaths.TempDir();
        var folder = CallFolder.Create(root, new DateTime(2026, 11, 1, 22, 0, 0));
        folder.WriteMetadata(new CallMetadata { App = "Discord", Title = "Appel Discord", StartedAt = new DateTime(2026, 11, 1, 22, 0, 0), Status = CallStatus.Pending });
        using (var opus = new OpusRecordingWriter(folder.AudioPath, 32000, "Test"))
        using (var envelope = new EnvelopeWriter(folder.EnvelopePath))
        {
            var mixer = new CallMixer(0, new MuteGate(false), [opus, envelope], TimeSpan.Zero);
            var me = mixer.AddTrack("mic", TrackRole.Me);
            me.Write(CodecTests.Sine(700, 0.3f, 48000 * 2), 48000 * 2);
            me.PadTo(48000 * 10);
            mixer.Flush();
        }
        new DiscordCallData { Me = new DiscordCallMember { Name = "Emerick" } }.Save(folder.DiscordPath);

        var processor = new CallProcessor(() => new VoicePipelineTests.SegmentPerRegionTranscriber(), () => null);
        var result = await processor.ProcessAsync(folder, new ProcessingOptions("Emerick L.", "Participants", "fake", null), null, CancellationToken.None);
        Assert.Null(result.Metadata.MyName);
        Assert.Contains("] Emerick L. : phrase", File.ReadAllText(folder.TranscriptPath));
    }
}
