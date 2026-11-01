using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Echo.Core.Audio;

namespace Echo.Core.Discord;

/// <summary>An error reported by Discord (RPC error code, or IPC close code).</summary>
public sealed class DiscordRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;

    // RPC error codes.
    public const int OAuth2Error = 5000;
    public const int InvalidPayload = 4000;
    public const int InvalidPermissions = 4006;
    public const int InvalidClientId = 4007;
    public const int InvalidToken = 4009;

    // IPC close codes.
    public const int CloseInvalidClientId = 4000;
    public const int CloseTokenRevoked = 4003;
}

/// <summary>An event pushed by Discord, stamped with the <see cref="AudioClock"/> time it arrived.</summary>
public sealed record DiscordRpcEvent(string Name, JsonElement Data, long ReceivedAt100ns);

/// <summary>
/// RPC client for the Discord desktop app, over its local IPC pipe: handshake with an
/// application id, requests answered by nonce, events pushed by Discord.
/// </summary>
public sealed class DiscordRpcClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly DiscordIpcChannel _channel;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Channel<DiscordRpcEvent> _events = Channel.CreateUnbounded<DiscordRpcEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource<JsonElement> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoop;

    private DiscordRpcClient(Stream stream) => _channel = new DiscordIpcChannel(stream);

    /// <summary>Events pushed by Discord (DISPATCH), in arrival order.</summary>
    public ChannelReader<DiscordRpcEvent> Events => _events.Reader;

    /// <summary>Completes when the connection ends; faulted with the reason Discord gave.</summary>
    public Task Completion => _closed.Task;

    /// <summary>The Discord account logged in the desktop app (from the READY event).</summary>
    public DiscordUserInfo? User { get; private set; }

    /// <summary>
    /// Connects to the Discord desktop app with the id of the user's Discord application.
    /// Returns null when Discord is not running.
    /// </summary>
    public static async Task<DiscordRpcClient?> ConnectAsync(string clientId, DiscordPipeConnector connector, CancellationToken cancellationToken)
    {
        var stream = await connector(cancellationToken).ConfigureAwait(false);
        if (stream is null) return null;

        var client = new DiscordRpcClient(stream);
        try
        {
            await client._channel.WriteAsync(DiscordOpcode.Handshake, JsonSerializer.Serialize(new { v = 1, client_id = clientId }), cancellationToken).ConfigureAwait(false);
            client._readLoop = Task.Run(client.ReadLoopAsync);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var ready = await client._ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (ready.ValueKind == JsonValueKind.Object && ready.TryGetProperty("user", out var user)) client.User = DiscordUserInfo.Parse(user);
            return client;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new DiscordRpcException(0, "Discord ne répond pas.");
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Sends a command and returns the "data" of Discord's answer.</summary>
    public async Task<JsonElement> RequestAsync(string command, object? args = null, string? evt = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        string nonce = Guid.NewGuid().ToString();
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = answer;
        try
        {
            if (_closed.Task.IsCompleted) throw ClosedException();
            string json = JsonSerializer.Serialize(new { cmd = command, args = args ?? new { }, evt, nonce }, JsonOptions);
            await _channel.WriteAsync(DiscordOpcode.Frame, json, cancellationToken).ConfigureAwait(false);

            Task finished;
            try
            {
                finished = await Task.WhenAny(answer.Task, _closed.Task)
                    .WaitAsync(timeout ?? TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new DiscordRpcException(0, $"Discord n'a pas répondu ({command}).");
            }
            if (finished == answer.Task) return await answer.Task.ConfigureAwait(false);
            throw ClosedException();
        }
        finally
        {
            _pending.TryRemove(nonce, out _);
        }
    }

    public Task SubscribeAsync(string evt, object? args = null, CancellationToken cancellationToken = default) =>
        RequestAsync("SUBSCRIBE", args, evt, cancellationToken: cancellationToken);

    public Task UnsubscribeAsync(string evt, object? args = null, CancellationToken cancellationToken = default) =>
        RequestAsync("UNSUBSCRIBE", args, evt, cancellationToken: cancellationToken);

    private Exception ClosedException() =>
        _closed.Task.Exception?.InnerException ?? new DiscordRpcException(0, "Connexion à Discord fermée.");

    private async Task ReadLoopAsync()
    {
        Exception? reason = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await _channel.ReadAsync(_cts.Token).ConfigureAwait(false);
                if (frame is not { } f) break;
                switch (f.Opcode)
                {
                    case DiscordOpcode.Frame:
                        HandleFrame(f.Json);
                        break;
                    case DiscordOpcode.Ping:
                        await _channel.WriteAsync(DiscordOpcode.Pong, f.Json, _cts.Token).ConfigureAwait(false);
                        break;
                    case DiscordOpcode.Close:
                        reason = ParseClose(f.Json);
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            reason = ex;
        }
        finally
        {
            reason ??= new DiscordRpcException(0, "Connexion à Discord fermée.");
            _ready.TrySetException(reason);
            _closed.TrySetException(reason);
            _ = _ready.Task.Exception; // observed: nobody may be waiting any more
            _ = _closed.Task.Exception;
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(reason);
                _ = pending.Task.Exception;
            }
            _events.Writer.TryComplete();
        }
    }

    private void HandleFrame(string json)
    {
        long receivedAt = AudioClock.Now100ns();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() : null;
        string? evt = root.TryGetProperty("evt", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
        string? nonce = root.TryGetProperty("nonce", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;

        if (nonce is not null && _pending.TryGetValue(nonce, out var answer))
        {
            if (evt == "ERROR") answer.TrySetException(ParseError(data));
            else answer.TrySetResult(data);
            return;
        }

        if (cmd == "DISPATCH" && evt is not null)
        {
            if (evt == "READY") _ready.TrySetResult(data);
            else if (evt == "ERROR") _ready.TrySetException(ParseError(data));
            else _events.Writer.TryWrite(new DiscordRpcEvent(evt, data, receivedAt));
        }
    }

    private static DiscordRpcException ParseError(JsonElement data)
    {
        int code = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("code", out var c) && c.TryGetInt32(out int v) ? v : 0;
        string message = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("message", out var m) ? m.GetString() ?? "Erreur Discord" : "Erreur Discord";
        return new DiscordRpcException(code, message);
    }

    private static DiscordRpcException ParseClose(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseError(doc.RootElement);
        }
        catch (JsonException)
        {
            return new DiscordRpcException(0, "Connexion fermée par Discord.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        await _channel.DisposeAsync().ConfigureAwait(false);
        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
        _closed.TrySetException(new DiscordRpcException(0, "Connexion à Discord fermée."));
        _ = _closed.Task.Exception;
        _cts.Dispose();
    }
}

/// <summary>A Discord account.</summary>
/// <param name="GlobalName">Display name chosen by the user (null if none).</param>
public sealed record DiscordUserInfo(string Id, string Username, string? GlobalName)
{
    public string DisplayName => string.IsNullOrWhiteSpace(GlobalName) ? Username : GlobalName!;

    public static DiscordUserInfo? Parse(JsonElement user)
    {
        if (user.ValueKind != JsonValueKind.Object) return null;
        string? id = user.TryGetProperty("id", out var i) ? i.GetString() : null;
        if (string.IsNullOrEmpty(id)) return null;
        string username = user.TryGetProperty("username", out var u) ? u.GetString() ?? id : id;
        string? global = user.TryGetProperty("global_name", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
        return new DiscordUserInfo(id, username, global);
    }
}
