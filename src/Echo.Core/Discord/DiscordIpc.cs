using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;

namespace Echo.Core.Discord;

/// <summary>Frame types of the Discord local IPC protocol.</summary>
public enum DiscordOpcode
{
    Handshake = 0,
    Frame = 1,
    Close = 2,
    Ping = 3,
    Pong = 4,
}

/// <summary>Opens a stream to the Discord desktop app; null when Discord is not running.</summary>
public delegate Task<Stream?> DiscordPipeConnector(CancellationToken cancellationToken);

/// <summary>
/// Framing of the IPC channel the Discord desktop app opens on the PC
/// (\\.\pipe\discord-ipc-0 to -9): [opcode int32 LE][length int32 LE][JSON UTF-8].
/// </summary>
public sealed class DiscordIpcChannel : IAsyncDisposable
{
    private const int MaxFrameBytes = 16 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public DiscordIpcChannel(Stream stream) => _stream = stream;

    public async Task WriteAsync(DiscordOpcode opcode, string json, CancellationToken cancellationToken)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, (int)opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), payload.Length);
        payload.CopyTo(frame, 8);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Next frame, or null when Discord closed the channel.</summary>
    public async Task<(DiscordOpcode Opcode, string Json)?> ReadAsync(CancellationToken cancellationToken)
    {
        byte[] header = new byte[8];
        if (!await ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false)) return null;
        var opcode = (DiscordOpcode)BinaryPrimitives.ReadInt32LittleEndian(header);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length < 0 || length > MaxFrameBytes) throw new InvalidDataException($"Trame Discord invalide ({length} octets).");

        byte[] payload = new byte[length];
        if (!await ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false)) return null;
        return (opcode, Encoding.UTF8.GetString(payload));
    }

    private async Task<bool> ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await _stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }

    /// <summary>
    /// Connects to the first Discord instance found (stable, PTB or Canary each open
    /// their own pipe, numbered from 0).
    /// </summary>
    public static async Task<Stream?> ConnectToDesktopAppAsync(CancellationToken cancellationToken)
    {
        for (int i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(150, cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
        return null;
    }
}
