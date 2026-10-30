using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ListenTeams.Core.Teams;

/// <summary>
/// Client for the Teams local WebSocket API (Teams → Settings → Privacy → Third-party
/// app API). It is the reliable way to know whether we are in a meeting and whether the
/// microphone is muted <em>in Teams</em>. Reconnects automatically.
/// </summary>
public sealed class TeamsLocalApiClient : IAsyncDisposable
{
    public const int Port = 8124;

    private readonly Func<string?> _loadToken;
    private readonly Action<string> _saveToken;
    private readonly string _appVersion;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private int _requestId;
    private TeamsApiState _state = TeamsApiState.Disconnected;

    public TeamsLocalApiClient(Func<string?> loadToken, Action<string> saveToken, string appVersion)
    {
        _loadToken = loadToken;
        _saveToken = saveToken;
        _appVersion = appVersion;
    }

    public event Action<TeamsApiState>? StateChanged;

    /// <summary>Raised when Teams sends a token, i.e. the user accepted the pairing.</summary>
    public event Action? Paired;

    public TeamsApiState State => _state;

    public bool HasToken => !string.IsNullOrEmpty(_loadToken());

    public static Uri BuildUri(string? token, string appVersion)
    {
        string query = string.Join('&',
            "token=" + Uri.EscapeDataString(token ?? string.Empty),
            "protocol-version=2.0.0",
            "manufacturer=ListenTeams",
            "device=ListenTeams",
            "app=ListenTeams",
            "app-version=" + Uri.EscapeDataString(appVersion));
        return new Uri($"ws://127.0.0.1:{Port}?{query}");
    }

    /// <summary>Connects and keeps the connection alive until cancelled.</summary>
    public async Task RunAsync(Func<bool> teamsRunning, CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromSeconds(3);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!teamsRunning())
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    await ConnectAndReadAsync(cancellationToken).ConfigureAwait(false);
                    retryDelay = TimeSpan.FromSeconds(3);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                    && ex is WebSocketException or HttpRequestException or IOException or OperationCanceledException)
                {
                    // Teams not ready yet, connection timeout, or the third-party API is
                    // disabled in the Teams settings: retry later.
                }
                finally
                {
                    SetState(TeamsApiState.Disconnected);
                }

                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromSeconds(Math.Min(30, retryDelay.TotalSeconds * 2));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ConnectAndReadAsync(CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(BuildUri(_loadToken(), _appVersion), connectTimeout.Token).ConfigureAwait(false);
        }

        _socket = socket;
        SetState(_state with { Connected = true });

        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Complete the close handshake (Teams closes when the API is disabled or Teams quits).
                    try
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken).ConfigureAwait(false);
                    }
                    catch (WebSocketException)
                    {
                    }
                    break;
                }
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                Handle(TeamsApiMessage.Parse(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length)));
                message.SetLength(0);
            }
        }
        finally
        {
            _socket = null;
        }
    }

    private void Handle(TeamsApiMessage message)
    {
        switch (message)
        {
            case TeamsApiMessage.TokenRefresh { Token.Length: > 0 } refresh:
                _saveToken(refresh.Token);
                Paired?.Invoke();
                break;

            case TeamsApiMessage.MeetingUpdate update:
                SetState(new TeamsApiState
                {
                    Connected = true,
                    HasMeetingState = update.HasMeetingState,
                    IsInMeeting = update.IsInMeeting,
                    IsMuted = update.IsMuted,
                    CanToggleMute = update.CanToggleMute,
                    CanPair = update.CanPair,
                });
                break;
        }
    }

    /// <summary>
    /// Asks Teams to pair with ListenTeams: Teams shows an "allow this app" prompt during a
    /// meeting. Teams only shows it when it receives a command; the command used for pairing
    /// is not executed ("Pairing response resulted in no action"). We use the most harmless
    /// one, background blur, which at worst only affects the user's own video.
    /// </summary>
    public Task<bool> RequestPairingAsync(CancellationToken cancellationToken = default) =>
        SendActionAsync("toggle-background-blur", cancellationToken);

    private async Task<bool> SendActionAsync(string action, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open) return false;

        string json = JsonSerializer.Serialize(new
        {
            action,
            parameters = new { },
            requestId = Interlocked.Increment(ref _requestId),
        });

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (WebSocketException)
        {
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void SetState(TeamsApiState state)
    {
        if (state == _state) return;
        _state = state;
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        var socket = _socket;
        if (socket is { State: WebSocketState.Open })
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
        _sendLock.Dispose();
    }
}
