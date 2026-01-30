using System.Buffers;
using System.Net.Sockets;
using System.Text;
using TinyProxy.Config;
using TinyProxy.Core;
using TinyProxy.Filter;
using TinyProxy.Logging;

namespace TinyProxy.Protocol.Http;

/// <summary>
/// Processes HTTP response headers from remote servers.
/// Aligns with tinyproxy C's process_server_headers() implementation.
/// </summary>
public sealed class HttpResponseProcessor
{
    private readonly ILogger _logger;
    private readonly Configuration _config;

    public HttpResponseProcessor(ILogger logger, Configuration config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Reads and processes response headers from server.
    /// Returns the processed headers dictionary and status code.
    /// </summary>
    public async Task<(Dictionary<string, ReadOnlySequence<byte>> headers, int statusCode)> ProcessResponseAsync(
        Socket serverSocket,
        CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        var totalReceived = 0;

        try
        {
            // Read response line: HTTP/1.x CODE STATUS\r\n
            var (statusLineLength, statusCode) = await ReadStatusLineAsync(serverSocket, buffer, token).ConfigureAwait(false);
            totalReceived += statusLineLength;

            // Read all headers
            var headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase);
            totalReceived += await ReadHeadersAsync(serverSocket, buffer, headers, token).ConfigureAwait(false);

            return (headers, statusCode);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads the HTTP status line from the server.
    /// Format: HTTP/1.1 200 OK\r\n
    /// </summary>
    private static async ValueTask<(int lineLength, int statusCode)> ReadStatusLineAsync(
        Socket socket,
        byte[] buffer,
        CancellationToken token)
    {
        var position = 0;

        while (position < buffer.Length)
        {
            var read = await socket.ReceiveAsync(
                new Memory<byte>(buffer, position, 1),
                SocketFlags.None,
                token).ConfigureAwait(false);

            if (read == 0)
            {
                throw new InvalidOperationException("Connection closed while reading status line");
            }

            // Check for LF (line end)
            if (buffer[position] == (byte)'\n')
            {
                // Parse status code
                var statusLine = Encoding.ASCII.GetString(buffer, 0, position);
                var parts = statusLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                
                if (parts.Length >= 2 && int.TryParse(parts[1], out var code))
                {
                    return (position + 1, code);
                }

                throw new InvalidOperationException($"Invalid status line: {statusLine}");
            }

            position++;
        }

        throw new InvalidOperationException("Status line too long");
    }

    /// <summary>
    /// Reads all headers from the socket.
    /// Returns number of bytes read.
    /// </summary>
    private static async ValueTask<int> ReadHeadersAsync(
        Socket socket,
        byte[] buffer,
        Dictionary<string, ReadOnlySequence<byte>> headers,
        CancellationToken token)
    {
        var position = 0;
        var lineStart = 0;
        var totalBytes = 0;

        while (position < buffer.Length)
        {
            var read = await socket.ReceiveAsync(
                new Memory<byte>(buffer, position, 1),
                SocketFlags.None,
                token).ConfigureAwait(false);

            if (read == 0)
            {
                throw new InvalidOperationException("Connection closed while reading headers");
            }

            position++;
            totalBytes++;

            // Check for CRLF (end of header line)
            if (position >= 2 && buffer[position - 2] == (byte)'\r' && buffer[position - 1] == (byte)'\n')
            {
                // Check for empty line (end of headers)
                if (position - lineStart <= 2)
                {
                    return totalBytes;
                }

                // Parse header line: Name: Value\r\n
                var headerLine = Encoding.ASCII.GetString(buffer, lineStart, position - lineStart - 2);
                var colonIndex = headerLine.IndexOf(':');

                if (colonIndex > 0)
                {
                    var name = headerLine.Substring(0, colonIndex).Trim();
                    var valueBytes = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes(
                        headerLine.Substring(colonIndex + 1).Trim()));

                    headers[name] = valueBytes;
                }

                lineStart = position;
            }
        }

        throw new InvalidOperationException("Headers too long");
    }

    /// <summary>
    /// Processes response headers for forwarding to client.
    /// Applies Via header, anonymous filtering, and hop-by-hop header removal.
    /// </summary>
    public byte[] ProcessHeadersForForwarding(
        Dictionary<string, ReadOnlySequence<byte>> headers,
        string clientIp)
    {
        using var ms = new MemoryStream();
        var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true);

        // Get hop-by-hop headers to remove
        var hopByHopHeaders = HeaderFilter.GetHopByHopHeaders();

        // Apply anonymous filter if enabled
        var anonymousFilter = new AnonymousFilter(_config.AnonymousAllowedHeaders);

        foreach (var header in headers)
        {
            var name = header.Key;

            // Skip hop-by-hop headers (aligns with tinyproxy C's remove_connection_headers)
            if (hopByHopHeaders.Contains(name))
            {
                continue;
            }

            // Apply anonymous filtering
            if (_config.IsAnonymousEnabled && !anonymousFilter.IsHeaderAllowed(name))
            {
                continue;
            }

            // Skip Via header (we'll add it back properly)
            if (name.Equals("Via", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Write header
            writer.Write($"{name}: ");
            writer.Flush();
            ms.Write(header.Value.ToArray());
            writer.Write("\r\n");
            writer.Flush();
        }

        // Add Via header if configured (aligns with tinyproxy C's write_via_header)
        if (_config.AddViaHeader)
        {
            string? existingVia = null;
            if (headers.TryGetValue("Via", out var viaValue))
            {
                existingVia = Encoding.ASCII.GetString(viaValue.ToArray()).Trim();
            }

            var hostname = _config.ViaProxyName ?? System.Net.Dns.GetHostName();
            var viaProxyName = string.IsNullOrEmpty(hostname) ? "unknown" : hostname;

            string viaHeader;
            if (!string.IsNullOrEmpty(existingVia))
            {
                viaHeader = $"Via: {existingVia}, 1.1 {viaProxyName} (tinyproxy-net)\r\n";
            }
            else
            {
                viaHeader = $"Via: 1.1 {viaProxyName} (tinyproxy-net)\r\n";
            }

            writer.Write(viaHeader);
        }

        writer.Write("\r\n"); // End of headers
        writer.Flush();

        return ms.ToArray();
    }
}
