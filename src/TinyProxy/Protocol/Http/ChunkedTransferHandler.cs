using System.Buffers;
using System.Net.Sockets;
using System.Text;
using TinyProxy.Core;

namespace TinyProxy.Protocol.Http;

/// <summary>
/// Handles HTTP chunked transfer encoding.
/// Aligns with tinyproxy C's pull_client_data_chunked() implementation.
/// </summary>
public sealed class ChunkedTransferHandler
{
    private readonly ILogger _logger;
    private const int BufferSize = 8192;

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
                // Read chunk size line
                var chunkSize = await ReadChunkSizeLineAsync(source, buffer, token).ConfigureAwait(false);
                if (chunkSize == 0)
                {
                    // End of chunked stream - read trailing CRLF
                    await ReadCrLfAsync(source, token).ConfigureAwait(false);
                    break;
                }

                // Read and forward the chunk data
                totalBytes += await ReadAndForwardChunkAsync(
                    source, destination, buffer, chunkSize, token).ConfigureAwait(false);

                // Read trailing CRLF after each chunk
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

        // Read until CRLF
        while (position < maxLineLength)
        {
            var read = await socket.ReceiveAsync(
                new Memory<byte>(buffer, position, 1),
                SocketFlags.None,
                token).ConfigureAwait(false);

            if (read == 0)
            {
                throw new InvalidOperationException("Connection closed while reading chunk size");
            }

            // Check for LF (line end)
            if (buffer[position] == (byte)'\n')
            {
                // Check for CR before LF
                if (position > 0 && buffer[position - 1] == (byte)'\r')
                {
                    position--; // Don't include CR in the parse
                }

                // Parse hex chunk size
                var hexLine = Encoding.ASCII.GetString(buffer, 0, position);
                if (!long.TryParse(hexLine, System.Globalization.NumberStyles.HexNumber, null, out var size))
                {
                    throw new InvalidOperationException($"Invalid chunk size: {hexLine}");
                }

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
        long bytesRemaining = chunkSize;
        long totalRead = 0;

        while (bytesRemaining > 0)
        {
            var toRead = (int)Math.Min(bytesRemaining, buffer.Length);
            var read = await source.ReceiveAsync(
                new Memory<byte>(buffer, 0, toRead),
                SocketFlags.None,
                token).ConfigureAwait(false);

            if (read == 0)
            {
                throw new InvalidOperationException("Connection closed while reading chunk data");
            }

            bytesRemaining -= read;
            totalRead += read;

            // Forward the data immediately
            await destination.SendAsync(
                new Memory<byte>(buffer, 0, read),
                SocketFlags.None,
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

            if (read == 0)
            {
                throw new InvalidOperationException("Connection closed while reading CRLF");
            }

            totalRead += read;
        }

        if (buffer[0] != (byte)'\r' || buffer[1] != (byte)'\n')
        {
            throw new InvalidOperationException("Expected CRLF");
        }
    }
}
