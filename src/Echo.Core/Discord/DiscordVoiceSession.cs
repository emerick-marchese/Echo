using System.Text.Json;
using Echo.Core.Audio;

namespace Echo.Core.Discord;

public enum DiscordLinkStatus
{
    /// <summary>No Discord application entered in the settings.</summary>
    NotConfigured,

    /// <summary>The Discord desktop app is not running.</summary>
    DiscordClosed,

    /// <summary>Connected to Discord, waiting for the user to authorize Echo.</summary>
    NeedsAuthorization,

    Connecting,

    Connected,

    Error,
}

/// <summary>Someone in the voice channel.</summary>
/// <param name="Name">Name shown in Discord: server nickname, else display name, else username.</param>
public sealed record DiscordParticipant(string Id, string Name, string Username, bool Speaking, bool Muted, bool Deafened);

/// <summary>What Echo knows from Discord right now.</summary>
public sealed record DiscordVoiceState(
    DiscordLinkStatus Status,
    string? Error,
    DiscordUserInfo? Me,
    string? ChannelId,
    string? ChannelName,
    string? GuildName,
    IReadOnlyList<DiscordParticipant> Participants,
    bool MeMuted,
    bool MeDeafened,
    bool PushToTalk,
    bool MeSpeaking)
{
    public static readonly DiscordVoiceState Initial = new(DiscordLinkStatus.NotConfigured, null, null, null, null, null, [], false, false, false, false);

    public bool Connected => Status == DiscordLinkStatus.Connected;

    /// <summary>Connected and in a voice channel (a call).</summary>
    public bool InVoice => Connected && ChannelId is not null;

    /// <summary>
    /// Whether Discord does NOT transmit the microphone: muted, deafened, or push-to-talk
    /// key not pressed. Null when unknown (not connected or not in a call).
    /// </summary>
    public bool? MicrophoneMuted => InVoice ? MeMuted || MeDeafened || (PushToTalk && !MeSpeaking) : null;
}

/// <summary>The user's Discord application (Developer Portal) and the tokens it obtained.</summary>
public sealed record DiscordCredentials(string ClientId, string ClientSecret, DiscordTokens? Tokens);

/// <summary>
/// Keeps Echo connected to the Discord desktop app, with the access the user granted to
/// their own Discord application (scopes rpc + rpc.voice.read): the voice channel the user
/// is in, the exact name of everyone in it, who is speaking (as Discord shows it), and the
/// user's own microphone state (mute, deafen, push-to-talk). Reconnects by itself.
/// </summary>
public sealed class DiscordVoiceSession
{
    public static readonly string[] Scopes = ["rpc", "rpc.voice.read"];

    private static readonly string[] ChannelEvents =
        ["VOICE_STATE_CREATE", "VOICE_STATE_UPDATE", "VOICE_STATE_DELETE", "SPEAKING_START", "SPEAKING_STOP"];

    private sealed class Member
    {
        public required string Id { get; init; }

        public string Name { get; set; } = "Participant";

        public string Username { get; set; } = string.Empty;

        public bool Speaking { get; set; }

        public bool Muted { get; set; }

        public bool Deafened { get; set; }

        public bool ServerMuted { get; set; }

        public bool Present { get; set; }

        public int Order { get; init; }
    }

    private readonly Func<DiscordCredentials?> _credentials;
    private readonly Action<DiscordTokens?> _saveTokens;
    private readonly DiscordPipeConnector _connector;
    private readonly DiscordOAuth _oauth;
    private readonly TimeSpan _retryDelay;
    private readonly object _lock = new();
    private readonly Dictionary<string, Member> _members = [];
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DiscordRpcClient? _client;
    private string? _clientId;
    private volatile bool _reconnect;
    private bool _authenticated;
    private int _nextOrder;
    private int _failedAttempts;

    // State, under _lock.
    private DiscordLinkStatus _status = DiscordLinkStatus.NotConfigured;
    private string? _error;
    private DiscordUserInfo? _me;
    private string? _channelId;
    private string? _channelName;
    private string? _guildName;
    private bool _settingsMute;
    private bool _settingsDeaf;
    private bool _pushToTalk;
    private DiscordVoiceState _state = DiscordVoiceState.Initial;

    public DiscordVoiceSession(
        Func<DiscordCredentials?> credentials, Action<DiscordTokens?> saveTokens,
        DiscordPipeConnector connector, DiscordOAuth oauth, TimeSpan? retryDelay = null)
    {
        _credentials = credentials;
        _saveTokens = saveTokens;
        _connector = connector;
        _oauth = oauth;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(5);
    }

    public DiscordVoiceState State
    {
        get
        {
            lock (_lock) return _state;
        }
    }

    /// <summary>Raised on every change (from the session's own task).</summary>
    public event Action<DiscordVoiceState>? StateChanged;

    /// <summary>
    /// Someone started (true) or stopped speaking, with the <see cref="AudioClock"/> time
    /// Discord reported it. Also raised for the user themself.
    /// </summary>
    public event Action<string, bool, long>? SpeakingChanged;

    /// <summary>The Discord application or its tokens changed: reconnect with them.</summary>
    public void Reconfigure()
    {
        _reconnect = true;
        Wake();
    }

    private void Wake() => Interlocked.Exchange(ref _wake, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    private Task WakeTask => Volatile.Read(ref _wake).Task;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var wake = WakeTask;
            var credentials = _credentials();
            if (credentials is null)
            {
                SetStatus(DiscordLinkStatus.NotConfigured, null);
                await WaitAsync(wake, Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var delay = _retryDelay;
            DiscordRpcClient? client = null;
            try
            {
                _reconnect = false;
                client = await DiscordRpcClient.ConnectAsync(credentials.ClientId, _connector, cancellationToken).ConfigureAwait(false);
                if (client is null)
                {
                    SetStatus(DiscordLinkStatus.DiscordClosed, null);
                    Interlocked.Increment(ref _failedAttempts);
                }
                else
                {
                    lock (_lock)
                    {
                        _client = client;
                        _clientId = credentials.ClientId;
                        _me = client.User;
                    }
                    await RunConnectedAsync(client, cancellationToken).ConfigureAwait(false);
                    wake = WakeTask;
                    if (!_reconnect) SetStatus(DiscordLinkStatus.DiscordClosed, null);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"Discord : {ex.Message}");
                bool badApplication = ex is DiscordRpcException { Code: DiscordRpcException.CloseInvalidClientId or DiscordRpcException.InvalidClientId };
                SetStatus(DiscordLinkStatus.Error, badApplication
                    ? "Client ID refusé par Discord : vérifiez l'identifiant de votre application (portail développeur → OAuth2)."
                    : ex.Message);
                if (badApplication) delay = TimeSpan.FromMinutes(1);
                wake = WakeTask;
                Interlocked.Increment(ref _failedAttempts);
            }
            finally
            {
                LeaveChannel(AudioClock.Now100ns());
                lock (_lock)
                {
                    _client = null;
                    _authenticated = false;
                }
                if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
            }

            if (!_reconnect) await WaitAsync(wake, delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunConnectedAsync(DiscordRpcClient client, CancellationToken cancellationToken)
    {
        Task<bool>? read = null;
        while (true)
        {
            var wake = WakeTask;
            var credentials = _credentials();
            if (_reconnect || credentials is null || credentials.ClientId != _clientId) return;

            if (!_authenticated)
            {
                if (credentials.Tokens is null)
                {
                    SetStatus(DiscordLinkStatus.NeedsAuthorization, StatusError(DiscordLinkStatus.NeedsAuthorization));
                }
                else
                {
                    SetStatus(DiscordLinkStatus.Connecting, null);
                    try
                    {
                        await AuthenticateAsync(client, credentials, cancellationToken).ConfigureAwait(false);
                    }
                    catch (DiscordAuthException ex)
                    {
                        Log.Warn($"Discord : {ex.Message}");
                        _saveTokens(null);
                        SetStatus(DiscordLinkStatus.NeedsAuthorization, ex.Message);
                        continue;
                    }
                    lock (_lock) _authenticated = true;
                    await StartTrackingAsync(client, cancellationToken).ConfigureAwait(false);
                    SetStatus(DiscordLinkStatus.Connected, null);
                    Log.Info($"Connecté à Discord ({_me?.DisplayName}).");
                }
            }

            read ??= client.Events.WaitToReadAsync(cancellationToken).AsTask();
            var done = await Task.WhenAny(wake, read, client.Completion).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (done == client.Completion)
            {
                if (client.Completion.Exception?.InnerException is DiscordRpcException { Code: DiscordRpcException.CloseTokenRevoked })
                {
                    _saveTokens(null);
                }
                return;
            }
            if (done != read) continue;

            bool more = await read.ConfigureAwait(false);
            read = null;
            if (!more) return;
            while (client.Events.TryRead(out var evt))
            {
                try
                {
                    await HandleEventAsync(client, evt, cancellationToken).ConfigureAwait(false);
                }
                catch (DiscordRpcException ex)
                {
                    Log.Warn($"Discord ({evt.Name}) : {ex.Message}");
                }
            }
        }
    }

    private string? StatusError(DiscordLinkStatus status)
    {
        lock (_lock) return _status == status ? _error : null;
    }

    /// <summary>
    /// Asks Discord to show its authorization window for the user's application, then
    /// exchanges the code for tokens. Returns the connected account.
    /// </summary>
    public async Task<DiscordUserInfo> AuthorizeAsync(CancellationToken cancellationToken)
    {
        var credentials = _credentials()
            ?? throw new InvalidOperationException("Renseignez d'abord le Client ID et le Client Secret de votre application Discord.");

        // Wait for the session to be connected with this application (it reconnects at once
        // after Reconfigure); give up as soon as an attempt fails.
        DiscordRpcClient? client = null;
        int failedBefore = Volatile.Read(ref _failedAttempts);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (client is null)
        {
            lock (_lock)
            {
                if (_client is { } c && _clientId == credentials.ClientId && !_reconnect) client = c;
            }
            if (client is not null) break;
            if (DateTime.UtcNow > deadline || Volatile.Read(ref _failedAttempts) > failedBefore)
            {
                var state = State;
                throw new InvalidOperationException(state.Status switch
                {
                    DiscordLinkStatus.DiscordClosed => "Discord n'est pas ouvert : lancez l'application Discord puis réessayez.",
                    DiscordLinkStatus.Error when state.Error is not null => state.Error,
                    _ => "Discord ne répond pas.",
                });
            }
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        JsonElement answer;
        try
        {
            // Discord now wants the redirect (declared in the application) from this step on.
            answer = await RequestAuthorizationAsync(client, credentials.ClientId, withRedirect: true, cancellationToken).ConfigureAwait(false);
        }
        catch (DiscordRpcException ex) when (ex.Code == DiscordRpcException.InvalidPayload)
        {
            // Older Discord versions refuse unknown arguments.
            answer = await RequestAuthorizationAsync(client, credentials.ClientId, withRedirect: false, cancellationToken).ConfigureAwait(false);
        }

        string code = answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("code", out var c2) ? c2.GetString() ?? string.Empty : string.Empty;
        if (code.Length == 0) throw new InvalidOperationException("Discord n'a pas donné de code d'autorisation.");

        var tokens = await _oauth.ExchangeCodeAsync(credentials.ClientId, credentials.ClientSecret, code, cancellationToken).ConfigureAwait(false);
        _saveTokens(tokens);
        lock (_lock) _authenticated = false;
        Wake();

        // Wait until the session is authenticated with the new tokens.
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var state = State;
            if (state.Connected && state.Me is not null) return state.Me;
            if (state.Status is DiscordLinkStatus.NeedsAuthorization or DiscordLinkStatus.Error && state.Error is not null)
            {
                throw new InvalidOperationException(state.Error);
            }
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Discord ne répond pas.");
    }

    private static async Task<JsonElement> RequestAuthorizationAsync(DiscordRpcClient client, string clientId, bool withRedirect, CancellationToken cancellationToken)
    {
        object args = withRedirect
            ? new { client_id = clientId, scopes = Scopes, redirect_uri = DiscordOAuth.RedirectUri }
            : new { client_id = clientId, scopes = Scopes };
        try
        {
            return await client.RequestAsync("AUTHORIZE", args, timeout: TimeSpan.FromMinutes(5), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (DiscordRpcException ex) when (ex.Code == DiscordRpcException.OAuth2Error)
        {
            throw new InvalidOperationException(DescribeAuthorizationError(ex.Message));
        }
    }

    internal static string DescribeAuthorizationError(string message)
    {
        if (message.Contains("denied", StringComparison.OrdinalIgnoreCase) || message.Contains("cancel", StringComparison.OrdinalIgnoreCase))
        {
            return "Autorisation refusée dans Discord.";
        }
        if (message.Contains("redirect", StringComparison.OrdinalIgnoreCase))
        {
            return $"Discord refuse l'adresse de redirection : dans le portail développeur, menu OAuth2 → Redirects, ajoutez exactement {DiscordOAuth.RedirectUri} (sans « / » à la fin), cliquez sur Save Changes, puis réessayez.";
        }
        if (message.Contains("scope", StringComparison.OrdinalIgnoreCase))
        {
            return "Discord refuse l'accès au salon vocal pour cette application : elle doit appartenir au compte Discord connecté sur ce PC (créez-la avec ce compte).";
        }
        return $"Discord a refusé l'autorisation : {message}";
    }

    private async Task AuthenticateAsync(DiscordRpcClient client, DiscordCredentials credentials, CancellationToken cancellationToken)
    {
        var tokens = credentials.Tokens!;
        if (tokens.NeedsRefresh(DateTimeOffset.UtcNow))
        {
            tokens = await RefreshAsync(credentials, tokens, cancellationToken).ConfigureAwait(false);
        }

        JsonElement answer;
        try
        {
            answer = await client.RequestAsync("AUTHENTICATE", new { access_token = tokens.AccessToken }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (DiscordRpcException ex) when (ex.Code is DiscordRpcException.InvalidToken or DiscordRpcException.InvalidPermissions or DiscordRpcException.OAuth2Error)
        {
            // Token revoked or expired early: renew it once.
            if (string.IsNullOrEmpty(tokens.RefreshToken)) throw new DiscordAuthException("Autorisation Discord expirée : reconnectez Echo à Discord.");
            tokens = await RefreshAsync(credentials, tokens, cancellationToken).ConfigureAwait(false);
            try
            {
                answer = await client.RequestAsync("AUTHENTICATE", new { access_token = tokens.AccessToken }, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (DiscordRpcException again)
            {
                throw new DiscordAuthException($"Discord refuse l'accès d'Echo ({again.Message}) : reconnectez Echo à Discord.");
            }
        }

        if (answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("user", out var user) && DiscordUserInfo.Parse(user) is { } me)
        {
            lock (_lock) _me = me;
        }
    }

    private async Task<DiscordTokens> RefreshAsync(DiscordCredentials credentials, DiscordTokens tokens, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(tokens.RefreshToken)) throw new DiscordAuthException("Autorisation Discord expirée : reconnectez Echo à Discord.");
        var renewed = await _oauth.RefreshAsync(credentials.ClientId, credentials.ClientSecret, tokens.RefreshToken, cancellationToken).ConfigureAwait(false);
        _saveTokens(renewed);
        return renewed;
    }

    private async Task StartTrackingAsync(DiscordRpcClient client, CancellationToken cancellationToken)
    {
        await client.SubscribeAsync("VOICE_CHANNEL_SELECT", cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            await client.SubscribeAsync("VOICE_SETTINGS_UPDATE", cancellationToken: cancellationToken).ConfigureAwait(false);
            ApplyVoiceSettings(await client.RequestAsync("GET_VOICE_SETTINGS", cancellationToken: cancellationToken).ConfigureAwait(false));
        }
        catch (DiscordRpcException ex)
        {
            Log.Warn($"Réglages vocaux Discord indisponibles : {ex.Message}");
        }

        var channel = await client.RequestAsync("GET_SELECTED_VOICE_CHANNEL", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (Str(channel, "id") is { } id) await JoinChannelAsync(client, id, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleEventAsync(DiscordRpcClient client, DiscordRpcEvent evt, CancellationToken cancellationToken)
    {
        var data = evt.Data;
        switch (evt.Name)
        {
            case "VOICE_CHANNEL_SELECT":
            {
                string? channelId = Str(data, "channel_id");
                string? current;
                lock (_lock) current = _channelId;
                if (channelId == current) return;
                if (current is not null)
                {
                    LeaveChannel(evt.ReceivedAt100ns);
                    foreach (string name in ChannelEvents)
                    {
                        try
                        {
                            await client.UnsubscribeAsync(name, new { channel_id = current }, cancellationToken).ConfigureAwait(false);
                        }
                        catch (DiscordRpcException)
                        {
                        }
                    }
                }
                if (channelId is not null) await JoinChannelAsync(client, channelId, cancellationToken).ConfigureAwait(false);
                Publish();
                break;
            }
            case "VOICE_SETTINGS_UPDATE":
                ApplyVoiceSettings(data);
                Publish();
                break;
            case "VOICE_STATE_CREATE":
            case "VOICE_STATE_UPDATE":
                lock (_lock) Upsert(data);
                Publish();
                break;
            case "VOICE_STATE_DELETE":
            {
                string? userId = data.TryGetProperty("user", out var user) ? Str(user, "id") : null;
                bool wasSpeaking = false;
                lock (_lock)
                {
                    if (userId is not null && _members.TryGetValue(userId, out var member))
                    {
                        wasSpeaking = member.Speaking;
                        member.Speaking = false;
                        member.Present = false;
                    }
                }
                if (wasSpeaking) SpeakingChanged?.Invoke(userId!, false, evt.ReceivedAt100ns);
                Publish();
                break;
            }
            case "SPEAKING_START":
            case "SPEAKING_STOP":
            {
                string? userId = Str(data, "user_id");
                bool speaking = evt.Name == "SPEAKING_START";
                lock (_lock)
                {
                    if (userId is null || (Str(data, "channel_id") is { } ch && ch != _channelId)) return;
                    if (!_members.TryGetValue(userId, out var member))
                    {
                        member = new Member { Id = userId, Order = _nextOrder++, Present = true };
                        _members[userId] = member;
                    }
                    member.Speaking = speaking;
                }
                SpeakingChanged?.Invoke(userId, speaking, evt.ReceivedAt100ns);
                Publish();
                break;
            }
        }
    }

    private async Task JoinChannelAsync(DiscordRpcClient client, string channelId, CancellationToken cancellationToken)
    {
        // Subscribe first, then read who is there, so nobody is missed in between.
        foreach (string name in ChannelEvents)
        {
            await client.SubscribeAsync(name, new { channel_id = channelId }, cancellationToken).ConfigureAwait(false);
        }
        var channel = await client.RequestAsync("GET_CHANNEL", new { channel_id = channelId }, cancellationToken: cancellationToken).ConfigureAwait(false);

        string? guildName = null;
        if (Str(channel, "guild_id") is { } guildId)
        {
            try
            {
                var guild = await client.RequestAsync("GET_GUILD", new { guild_id = guildId }, timeout: TimeSpan.FromSeconds(5), cancellationToken: cancellationToken).ConfigureAwait(false);
                guildName = Str(guild, "name");
            }
            catch (DiscordRpcException)
            {
            }
        }

        lock (_lock)
        {
            _channelId = channelId;
            _channelName = Str(channel, "name");
            _guildName = guildName;
            if (channel.ValueKind == JsonValueKind.Object && channel.TryGetProperty("voice_states", out var states) && states.ValueKind == JsonValueKind.Array)
            {
                foreach (var state in states.EnumerateArray()) Upsert(state);
            }
        }
        Log.Info($"Discord : salon vocal « {Str(channel, "name")} »{(guildName is null ? string.Empty : $" ({guildName})")}.");
        Publish();
    }

    private void LeaveChannel(long at100ns)
    {
        List<string> stopped;
        lock (_lock)
        {
            stopped = _members.Values.Where(m => m.Speaking).Select(m => m.Id).ToList();
            _members.Clear();
            _channelId = null;
            _channelName = null;
            _guildName = null;
        }
        foreach (string id in stopped) SpeakingChanged?.Invoke(id, false, at100ns);
        Publish();
    }

    /// <remarks>Call with <see cref="_lock"/> held.</remarks>
    private void Upsert(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("user", out var userElement)) return;
        if (DiscordUserInfo.Parse(userElement) is not { } user) return;
        if (!_members.TryGetValue(user.Id, out var member))
        {
            member = new Member { Id = user.Id, Order = _nextOrder++ };
            _members[user.Id] = member;
        }
        string? nick = Str(data, "nick");
        member.Name = string.IsNullOrWhiteSpace(nick) ? user.DisplayName : nick!;
        member.Username = user.Username;
        member.Present = true;
        if (data.TryGetProperty("voice_state", out var voice) && voice.ValueKind == JsonValueKind.Object)
        {
            member.Muted = Bool(voice, "self_mute") || Bool(voice, "mute") || Bool(voice, "suppress");
            member.ServerMuted = Bool(voice, "mute") || Bool(voice, "suppress");
            member.Deafened = Bool(voice, "self_deaf") || Bool(voice, "deaf");
        }
    }

    private void ApplyVoiceSettings(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object) return;
        lock (_lock)
        {
            if (settings.TryGetProperty("mute", out _)) _settingsMute = Bool(settings, "mute");
            if (settings.TryGetProperty("deaf", out _)) _settingsDeaf = Bool(settings, "deaf");
            if (settings.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.Object)
            {
                _pushToTalk = Str(mode, "type") == "PUSH_TO_TALK";
            }
        }
    }

    private void SetStatus(DiscordLinkStatus status, string? error)
    {
        lock (_lock)
        {
            if (_status == status && _error == error) return;
            _status = status;
            _error = error;
            if (status is DiscordLinkStatus.NotConfigured or DiscordLinkStatus.DiscordClosed) _me = null;
        }
        Publish();
    }

    private void Publish()
    {
        DiscordVoiceState state;
        lock (_lock)
        {
            Member? me = _me is null ? null : _members.GetValueOrDefault(_me.Id);
            var participants = _members.Values
                .Where(m => m.Present && m.Id != _me?.Id)
                .OrderBy(m => m.Order)
                .Select(m => new DiscordParticipant(m.Id, m.Name, m.Username, m.Speaking, m.Muted, m.Deafened))
                .ToList();
            state = new DiscordVoiceState(
                _status, _error, _me, _channelId, _channelName, _guildName, participants,
                MeMuted: _settingsMute || (me?.Muted ?? false),
                MeDeafened: _settingsDeaf || (me?.Deafened ?? false),
                PushToTalk: _pushToTalk,
                MeSpeaking: me?.Speaking ?? false);
            _state = state;
        }
        StateChanged?.Invoke(state);
    }

    private static async Task WaitAsync(Task wake, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await wake.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
