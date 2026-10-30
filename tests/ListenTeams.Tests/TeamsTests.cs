using System.Net;
using System.Net.WebSockets;
using System.Text;
using ListenTeams.Core.Teams;

namespace ListenTeams.Tests;

public class TeamsApiMessageTests
{
    [Fact]
    public void Parses_paired_meeting_update()
    {
        var message = TeamsApiMessage.Parse("""
            {"meetingUpdate":{"meetingState":{"isMuted":true,"isVideoOn":false,"isHandRaised":false,"isInMeeting":true,"isRecordingOn":false,"isBackgroundBlurred":false,"isSharing":false,"hasUnreadMessages":false},
             "meetingPermissions":{"canToggleMute":true,"canToggleVideo":true,"canToggleHand":true,"canToggleBlur":false,"canLeave":true,"canReact":true,"canToggleShareTray":true,"canToggleChat":true,"canStopSharing":false,"canPair":false}}}
            """);
        var update = Assert.IsType<TeamsApiMessage.MeetingUpdate>(message);
        Assert.True(update.HasMeetingState);
        Assert.True(update.IsInMeeting);
        Assert.True(update.IsMuted);
        Assert.True(update.CanToggleMute);
        Assert.False(update.CanPair);
    }

    [Fact]
    public void Parses_unpaired_permissions_only_update()
    {
        var update = Assert.IsType<TeamsApiMessage.MeetingUpdate>(TeamsApiMessage.Parse(
            """{"meetingUpdate":{"meetingPermissions":{"canToggleMute":false,"canPair":true}}}"""));
        Assert.False(update.HasMeetingState);
        Assert.False(update.IsMuted);
        Assert.True(update.CanPair);
    }

    [Fact]
    public void Parses_token_and_responses()
    {
        Assert.Equal("abc", Assert.IsType<TeamsApiMessage.TokenRefresh>(TeamsApiMessage.Parse("""{"tokenRefresh":"abc"}""")).Token);
        var response = Assert.IsType<TeamsApiMessage.Response>(TeamsApiMessage.Parse("""{"requestId":2,"response":"Success"}"""));
        Assert.Equal(2, response.RequestId);
        Assert.Equal("Success", response.Text);
        Assert.IsType<TeamsApiMessage.Unknown>(TeamsApiMessage.Parse("not json"));
    }

    [Fact]
    public void Connection_uri_contains_protocol_and_escaped_token()
    {
        var uri = TeamsLocalApiClient.BuildUri("a b", "1.2.3").AbsoluteUri;
        Assert.StartsWith("ws://127.0.0.1:8124/?token=a%20b&protocol-version=2.0.0", uri);
        Assert.Contains("app=ListenTeams", uri);
    }

    [Fact]
    public void State_suggests_meeting_from_state_or_permissions()
    {
        Assert.True(new TeamsApiState { Connected = true, HasMeetingState = true, IsInMeeting = true }.SuggestsMeeting);
        Assert.False(new TeamsApiState { Connected = true, HasMeetingState = true, IsInMeeting = false, CanToggleMute = true }.SuggestsMeeting);
        Assert.True(new TeamsApiState { Connected = true, CanPair = true }.SuggestsMeeting);
        Assert.False(new TeamsApiState { Connected = false, CanPair = true }.SuggestsMeeting);
    }
}

public class TeamsLocalApiClientTests
{
    [Fact]
    public async Task Talks_to_a_fake_Teams_server()
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{TeamsLocalApiClient.Port}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException)
        {
            return; // port busy on this machine: nothing to test against
        }

        string? savedToken = null;
        var client = new TeamsLocalApiClient(() => savedToken, t => savedToken = t, "9.9.9");
        var states = new List<TeamsApiState>();
        client.StateChanged += s => { lock (states) states.Add(s); };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = client.RunAsync(() => true, cts.Token);

        var context = await listener.GetContextAsync().WaitAsync(cts.Token);
        Assert.Contains("protocol-version=2.0.0", context.Request.Url!.Query);
        var ws = (await context.AcceptWebSocketAsync(null)).WebSocket;

        await Send(ws, """{"meetingUpdate":{"meetingPermissions":{"canPair":true,"canToggleMute":true}}}""");
        await WaitUntil(() => client.State.CanPair, cts.Token);
        Assert.True(client.State.Connected);
        Assert.False(client.HasToken);

        Assert.True(await client.RequestPairingAsync(cts.Token));
        string request = await Receive(ws, cts.Token);
        Assert.Contains("\"action\":\"toggle-background-blur\"", request);
        Assert.Contains("\"requestId\":1", request);

        bool paired = false;
        client.Paired += () => paired = true;
        await Send(ws, """{"tokenRefresh":"secret-token"}""");
        await Send(ws, """{"meetingUpdate":{"meetingState":{"isMuted":true,"isInMeeting":true},"meetingPermissions":{"canToggleMute":true}}}""");
        await WaitUntil(() => client.State.HasMeetingState, cts.Token);

        Assert.True(paired);
        Assert.Equal("secret-token", savedToken);
        Assert.True(client.State.IsMuted);
        Assert.True(client.State.IsInMeeting);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
        await WaitUntil(() => !client.State.Connected, cts.Token);
        cts.Cancel();
        await run;
        await client.DisposeAsync();
    }

    private static Task Send(WebSocket ws, string json) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<string> Receive(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(buffer, ct);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(20, ct);
    }
}

public class CallDetectorTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Api_meeting_starts_recording_immediately_and_ends_after_delay()
    {
        var detector = new CallDetector();
        detector.SetTeamsRunning(true);
        Assert.Equal(CallEvent.None, detector.Tick(T0));

        detector.SetApiState(new TeamsApiState { Connected = true, HasMeetingState = true, IsInMeeting = true });
        Assert.Equal(CallEvent.Started, detector.Tick(T0.AddSeconds(1)));
        Assert.True(detector.IsRecording);

        detector.SetApiState(new TeamsApiState { Connected = true, HasMeetingState = true, IsInMeeting = false });
        Assert.Equal(CallEvent.None, detector.Tick(T0.AddSeconds(10)));
        Assert.Equal(CallEvent.None, detector.Tick(T0.AddSeconds(12)));
        Assert.Equal(CallEvent.Ended, detector.Tick(T0.AddSeconds(14)));
    }

    [Fact]
    public void Microphone_usage_alone_starts_after_debounce()
    {
        var detector = new CallDetector();
        detector.SetTeamsRunning(true);
        detector.SetMicInUse(true);
        Assert.Equal(CallEvent.None, detector.Tick(T0));
        Assert.Equal(CallEvent.None, detector.Tick(T0.AddSeconds(1)));
        Assert.Equal(CallEvent.Started, detector.Tick(T0.AddSeconds(2)));
    }

    [Fact]
    public void Short_microphone_blip_does_not_start_a_recording()
    {
        var detector = new CallDetector();
        detector.SetTeamsRunning(true);
        detector.SetMicInUse(true);
        detector.Tick(T0);
        detector.SetMicInUse(false);
        Assert.Equal(CallEvent.None, detector.Tick(T0.AddSeconds(1)));
        Assert.Equal(CallEvent.None, detector.Tick(T0.AddSeconds(5)));
        Assert.False(detector.IsRecording);
    }

    [Fact]
    public void Short_signal_loss_does_not_split_the_call()
    {
        var detector = new CallDetector();
        detector.SetTeamsRunning(true);
        detector.SetMicInUse(true);
        detector.Tick(T0);
        Assert.Equal(CallEvent.Started, detector.Tick(T0.AddSeconds(3)));
        detector.SetMicInUse(false);
        detector.Tick(T0.AddSeconds(4));
        detector.SetMicInUse(true);
        Assert.Equal(CallEvent.None, detector.Tick(T0.AddSeconds(6)));
        Assert.True(detector.IsRecording);
    }

    [Fact]
    public void Manual_stop_is_respected_until_the_call_ends()
    {
        var detector = new CallDetector();
        detector.SetTeamsRunning(true);
        detector.SetMicInUse(true);
        detector.Tick(T0);
        detector.Tick(T0.AddSeconds(3));
        Assert.Equal(CallEvent.Ended, detector.StopManually());
        Assert.Equal(CallEvent.None, detector.Tick(T0.AddSeconds(10)));

        detector.SetMicInUse(false);
        detector.Tick(T0.AddSeconds(20));
        detector.Tick(T0.AddSeconds(30));
        detector.SetMicInUse(true);
        detector.Tick(T0.AddSeconds(40));
        Assert.Equal(CallEvent.Started, detector.Tick(T0.AddSeconds(43))); // next call is recorded again
    }

    [Fact]
    public void Manual_recording_ignores_call_signals()
    {
        var detector = new CallDetector();
        Assert.Equal(CallEvent.Started, detector.StartManually());
        Assert.True(detector.IsManual);
        Assert.Equal(CallEvent.None, detector.Tick(T0.AddMinutes(5)));
        Assert.Equal(CallEvent.Ended, detector.StopManually());
    }

    [Fact]
    public void Nothing_happens_when_Teams_is_closed()
    {
        var detector = new CallDetector();
        detector.SetTeamsRunning(false);
        detector.SetMicInUse(true);
        detector.Tick(T0);
        Assert.Equal(CallEvent.None, detector.Tick(T0.AddSeconds(10)));
    }
}

public class MuteResolverTests
{
    private static readonly TeamsApiState PairedMuted = new() { Connected = true, HasMeetingState = true, IsInMeeting = true, IsMuted = true };

    [Fact]
    public void Paired_api_wins_over_window()
    {
        var decision = MuteResolver.Resolve(PairedMuted, apiPaired: true, windowMuted: false, recordWhenUnknown: true);
        Assert.Equal(new MuteDecision(true, MuteSource.TeamsApi), decision);
    }

    [Fact]
    public void Unpaired_api_is_not_trusted_for_mute()
    {
        var decision = MuteResolver.Resolve(PairedMuted, apiPaired: false, windowMuted: false, recordWhenUnknown: false);
        Assert.Equal(new MuteDecision(false, MuteSource.TeamsWindow), decision);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Unknown_state_follows_setting(bool recordWhenUnknown, bool expectedMuted)
    {
        var decision = MuteResolver.Resolve(TeamsApiState.Disconnected, false, null, recordWhenUnknown);
        Assert.Equal(new MuteDecision(expectedMuted, MuteSource.Unknown), decision);
    }

    [Theory]
    [InlineData("Mute mic (Ctrl+Shift+M)", false)]
    [InlineData("Unmute mic (Ctrl+Shift+M)", true)]
    [InlineData("Désactiver le micro (Ctrl+Maj+M)", false)]
    [InlineData("Activer le micro (Ctrl+Maj+M)", true)]
    [InlineData("Couper le micro", false)]
    [InlineData("Réactiver le micro", true)]
    [InlineData("Desactivar micrófono", false)]
    [InlineData("Activar micrófono", true)]
    [InlineData("Caméra", null)]
    [InlineData("", null)]
    public void Microphone_button_label_is_understood(string label, bool? muted)
    {
        Assert.Equal(muted, MuteResolver.ParseMicButtonLabel(label));
    }
}
