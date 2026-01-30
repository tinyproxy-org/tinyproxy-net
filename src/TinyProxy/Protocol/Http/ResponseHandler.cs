using System.Buffers;
using System.Net.Sockets;
using System.Text;
using TinyProxy.Config;
using TinyProxy.Core;

namespace TinyProxy.Protocol.Http;

/// <summary>
/// Handles HTTP response processing.
/// </summary>
public sealed class ResponseHandler
{
    private readonly ILogger _logger;
    private readonly Configuration _config;
    private const int BufferSize = 8192;

    public ResponseHandler(ILogger logger, Configuration config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Reads response from server and forwards to client.
    /// </summary>
    public async Task ForwardAsync(Socket server, Socket client, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            int received;
            var totalBytes = 0L;

            while ((received = await server.ReceiveAsync(buffer, SocketFlags.None, token).ConfigureAwait(false)) > 0)
            {
                await client.SendAsync(buffer.AsMemory(0, received), SocketFlags.None, token).ConfigureAwait(false);
                totalBytes += received;
            }

            if (_config.Verbose)
            {
                _logger.LogInfo($"Response forwarded: {totalBytes} bytes");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Parses HTTP status code from response bytes.
    /// </summary>
    public static bool TryParseStatus(ReadOnlySpan<byte> buffer, out int code, out string status)
    {
        code = 0;
        status = "Unknown";

        // Find end of status line
        var lineEnd = buffer.IndexOf((byte)'\n');
        if (lineEnd < 0) return false;

        var line = buffer.Slice(0, lineEnd);

        // Skip "HTTP/1.x "
        var spaceIndex = line.IndexOf((byte)' ');
        if (spaceIndex < 0) return false;

        var afterVersion = line.Slice(spaceIndex + 1);

        // Find next space
        var nextSpace = afterVersion.IndexOf((byte)' ');
        if (nextSpace < 0) return false;

        // Parse code
        var codeSpan = afterVersion.Slice(0, nextSpace);
        if (!int.TryParse(Encoding.ASCII.GetString(codeSpan), out code)) return false;

        // Get status text
        var statusSpan = afterVersion.Slice(nextSpace + 1).TrimEnd((byte)'\r');
        status = Encoding.ASCII.GetString(statusSpan);

        return true;
    }
}
