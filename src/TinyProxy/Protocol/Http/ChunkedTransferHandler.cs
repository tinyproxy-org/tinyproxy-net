namespace TinyProxy.Protocol.Http;

/// <summary>
/// Handles HTTP chunked transfer encoding.
/// </summary>
public sealed class ChunkedTransferHandler
{
    private readonly ILogger _logger;
    private const int BufferSize = 8192;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChunkedTransferHandler"/> class.
    /// </summary>
    public ChunkedTransferHandler(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Reads chunked data from source and forwards to destination.
    /// Returns the total number of bytes transferred.
    /// </summary>
    public async ValueTask<long> ForwardChunkedAsync(
        Socket source,
        Socket destination,
        CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long totalBytes = 0;

        try
        {
            while (true)
            {
                var chunkSize = await ReadChunkSizeLineAsync(source, buffer, token).ConfigureAwait(false);
                if (chunkSize == 0)
                {
                    await ReadCrLfAsync(source, token).ConfigureAwait(false);
                    break;
                }

                totalBytes += await ReadAndForwardChunkAsync(
                    source, destination, buffer, chunkSize, token).ConfigureAwait(false);

                await ReadCrLfAsync(source, token).ConfigureAwait(false);
            }

            return totalBytes;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads a chunk size line from the socket.
    /// Chunk size lines are hexadecimal numbers followed by CRLF.
    /// </summary>
    private static async ValueTask<long> ReadChunkSizeLineAsync(
        Socket socket,
        byte[] buffer,
        CancellationToken token)
    {
        var position = 0;
        var maxLineLength = buffer.Length;

        while (position < maxLineLength)
        {
            var read = await socket.ReceiveAsync(
                new Memory<byte>(buffer, position, 1),
                SocketFlags.None,
                token).ConfigureAwait(false);

            if (read == 0) throw new InvalidOperationException("Connection closed while reading chunk size");

            if (buffer[position] == (byte)'\n')
            {
                if (position > 0 && buffer[position - 1] == (byte)'\r') position--;

                var hexLine = Encoding.ASCII.GetString(buffer, 0, position);
                if (!long.TryParse(hexLine, System.Globalization.NumberStyles.HexNumber, null, out var size)) throw new InvalidOperationException($"Invalid chunk size: {hexLine}");

                return size;
            }

            position++;
        }

        throw new InvalidOperationException("Chunk size line too long");
    }

    /// <summary>
    /// Reads and forwards a chunk of data.
    /// </summary>
    private static async ValueTask<long> ReadAndForwardChunkAsync(
        Socket source,
        Socket destination,
        byte[] buffer,
        long chunkSize,
        CancellationToken token)
    {
        var bytesRemaining = chunkSize;
        long totalRead = 0;

        while (bytesRemaining > 0)
        {
            var toRead = (int)Math.Min(bytesRemaining, buffer.Length);
            var read = await source.ReceiveAsync(
                new Memory<byte>(buffer, 0, toRead),
                SocketFlags.None,
                token).ConfigureAwait(false);

            if (read == 0) throw new InvalidOperationException("Connection closed while reading chunk data");

            bytesRemaining -= read;
            totalRead += read;

            await destination.SendAllAsync(
                new Memory<byte>(buffer, 0, read),
                token).ConfigureAwait(false);
        }

        return totalRead;
    }

    /// <summary>
    /// Reads a CRLF from the socket.
    /// </summary>
    private static async ValueTask ReadCrLfAsync(Socket socket, CancellationToken token)
    {
        var buffer = new byte[2];
        var totalRead = 0;

        while (totalRead < 2)
        {
            var read = await socket.ReceiveAsync(
                new Memory<byte>(buffer, totalRead, 2 - totalRead),
                SocketFlags.None,
                token).ConfigureAwait(false);

            if (read == 0) throw new InvalidOperationException("Connection closed while reading CRLF");

            totalRead += read;
        }

        if (buffer[0] != (byte)'\r' || buffer[1] != (byte)'\n') throw new InvalidOperationException("Expected CRLF");
    }
}