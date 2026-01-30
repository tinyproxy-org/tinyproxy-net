using System.Buffers;
using System.Net.Sockets;
using System.Text;
using TinyProxy.Config;
using TinyProxy.Core;
using TinyProxy.Filter;
using TinyProxy.Logging;
using TinyProxy.Metrics;

namespace TinyProxy.Protocol.Http;

/// <summary>
/// Forwards HTTP requests to target servers.
/// </summary>
public sealed class HttpForwarder
{
    private readonly ILogger _logger;
    private readonly Configuration _config;
    private readonly Stats _stats;
    private readonly AccessLogger _accessLogger;
    private readonly string _clientIp;
    private const int BufferSize = 8192;

    public HttpForwarder(
        ILogger logger,
        Configuration config,
        Stats stats,
        AccessLogger accessLogger,
        string clientIp)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _accessLogger = accessLogger ?? throw new ArgumentNullException(nameof(accessLogger));
        _clientIp = clientIp ?? "unknown";
    }

    public async ValueTask ForwardAsync(
        Core.Connection connection,
        HttpRequest request,
        CancellationToken token)
    {
        int statusCode = 200;
        long bytesSent = 0;

        if (!request.TryGetTarget(out var host, out var port))
        {
            _stats.IncrementFailedRequests();
            await SendErrorAsync(connection.ClientSocket, 400, "Bad Request", "Invalid target host");
            LogAccess(connection, request, 400, 0);
            return;
        }

        // Check request body size limit
        if (_config.MaxRequestSize > 0 && request.ContentLength.HasValue && request.ContentLength.Value > _config.MaxRequestSize)
        {
            _stats.IncrementFailedRequests();
            await SendErrorAsync(connection.ClientSocket, 413, "Payload Too Large",
                $"Request body exceeds maximum allowed size of {_config.MaxRequestSize} bytes");
            LogAccess(connection, request, 413, 0);
            return;
        }

        if (_config.Verbose)
        {
            _logger.LogInfo($"Forwarding {HttpMethodParser.ToHttpString(request.Method)} {request.Uri}");
        }

        try
        {
            Socket serverSocket;
            long bytesReceived = 0;

            // Check if upstream proxy is configured
            if (_config.UpstreamProxy != null)
            {
                serverSocket = await ConnectViaUpstreamAsync(host, port, token).ConfigureAwait(false);
            }
            else
            {
                serverSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await serverSocket.ConnectAsync(host, port, _config.Timeout, token).ConfigureAwait(false);
            }

            try
            {
                // Build modified request
                var requestBuffer = BuildForwardRequest(request, host, port);
                await serverSocket.SendAsync(requestBuffer, SocketFlags.None, token).ConfigureAwait(false);

                // Send any body data
                if (request.Body.Length > 0 && request.ContentLength.HasValue && request.ContentLength.Value > 0)
                {
                    await SendBodyAsync(serverSocket, request.Body, token).ConfigureAwait(false);
                }

                // Read response from server and forward to client
                (bytesSent, bytesReceived) = await ForwardResponseAsync(serverSocket, connection.ClientSocket, token).ConfigureAwait(false);

                _stats.AddBytesSent(bytesSent);
                _stats.AddBytesReceived(bytesReceived);
            }
            finally
            {
                serverSocket.Dispose();
            }
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            _stats.IncrementFailedRequests();
            statusCode = 502;
            await SendErrorAsync(connection.ClientSocket, 502, "Bad Gateway", $"Could not connect to {host}:{port}");
        }
        catch (TimeoutException)
        {
            _stats.IncrementFailedRequests();
            statusCode = 504;
            await SendErrorAsync(connection.ClientSocket, 504, "Gateway Timeout", "Server response timeout");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Forward error: {ex.Message}");
            _stats.IncrementFailedRequests();
            statusCode = 502;
            await SendErrorAsync(connection.ClientSocket, 502, "Bad Gateway", ex.Message);
        }
        finally
        {
            LogAccess(connection, request, statusCode, bytesSent);
        }
    }

    private async Task<Socket> ConnectViaUpstreamAsync(string targetHost, int targetPort, CancellationToken token)
    {
        var upstream = _config.UpstreamProxy!;

        // Connect to upstream proxy
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(upstream.Host, upstream.Port, _config.Timeout, token).ConfigureAwait(false);

        // Note: For HTTP proxying, the request will be formatted with absolute URI
        // The upstream proxy will handle the actual connection to target
        return socket;
    }

    private byte[] BuildForwardRequest(HttpRequest request, string host, int port)
    {
        using var ms = new MemoryStream();
        var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true);

        // Request line - use absolute URI for proxy
        // Aligns with tinyproxy C which builds proper absolute URIs
        var method = HttpMethodParser.ToHttpString(request.Method);
        var absoluteUri = GetAbsoluteUri(request.Uri, host, port);

        writer.Write($"{method} {absoluteUri} {request.Version}\r\n");

        // Headers - filter and modify
        var hopByHopHeaders = HeaderFilter.GetHopByHopHeaders();

        // Apply anonymous filter if enabled (aligns with tinyproxy C's anonymous.c)
        var anonymousFilter = new AnonymousFilter(_config.AnonymousAllowedHeaders);

        foreach (var header in request.Headers)
        {
            var name = header.Key;

            // Skip hop-by-hop headers
            if (hopByHopHeaders.Contains(name))
            {
                continue;
            }

            // Apply anonymous filtering (aligns with tinyproxy C's anonymous_search)
            if (_config.IsAnonymousEnabled && !anonymousFilter.IsHeaderAllowed(name))
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

        // Add proxy authentication for upstream proxy
        if (_config.UpstreamProxy?.Username != null)
        {
            var credentials = $"{_config.UpstreamProxy.Username}:{_config.UpstreamProxy.Password}";
            var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes(credentials));
            writer.Write($"Proxy-Authorization: Basic {encoded}\r\n");
        }

        // Add Via header if configured
        // Aligns with tinyproxy C which properly formats Via header
        if (_config.AddViaHeader)
        {
            writer.Write("Via: 1.1 tinyproxy-net\r\n");
        }

        // Add X-Forwarded-For with actual client IP
        writer.Write($"X-Forwarded-For: {_clientIp}\r\n");
        writer.Write($"X-Forwarded-Host: {host}\r\n");
        writer.Write($"X-Forwarded-Proto: http\r\n");

        // Add X-Tinyproxy header if configured
        // Aligns with tinyproxy C's AddXTinyproxy option
        if (_config.AddXTinyproxyHeader)
        {
            writer.Write("X-Tinyproxy: tinyproxy-net\r\n");
        }

        writer.Write("\r\n"); // End of headers
        writer.Flush();

        return ms.ToArray();
    }

    private static async ValueTask SendBodyAsync(Socket socket, ReadOnlySequence<byte> body, CancellationToken token)
    {
        foreach (var segment in body)
        {
            await socket.SendAsync(segment, SocketFlags.None, token).ConfigureAwait(false);
        }
    }

    private async Task<(long sent, long received)> ForwardResponseAsync(Socket server, Socket client, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long totalSent = 0;
        long totalReceived = 0;

        try
        {
            int received;
            while ((received = await server.ReceiveAsync(buffer, SocketFlags.None, token).ConfigureAwait(false)) > 0)
            {
                totalReceived += received;
                await client.SendAsync(buffer.AsMemory(0, received), SocketFlags.None, token).ConfigureAwait(false);
                totalSent += received;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (totalSent, totalReceived);
    }

    private async ValueTask SendErrorAsync(Socket socket, int code, string status, string message)
    {
        var html = $@"
<!DOCTYPE html>
<html>
<head><title>{code} {status}</title></head>
<body>
<h1>{code} {status}</h1>
<p>{System.Net.WebUtility.HtmlEncode(message)}</p>
<hr>
<address>TinyProxy.NET</address>
</body>
</html>";

        var response = $"HTTP/1.1 {code} {status}\r\n" +
                       $"Content-Type: text/html\r\n" +
                       $"Content-Length: {Encoding.UTF8.GetByteCount(html)}\r\n" +
                       $"Connection: close\r\n" +
                       $"\r\n{html}";

        var buffer = Encoding.UTF8.GetBytes(response);
        await socket.SendAsync(buffer, SocketFlags.None).ConfigureAwait(false);
    }

    private void LogAccess(Core.Connection connection, HttpRequest request, int statusCode, long bytesSent)
    {
        var method = HttpMethodParser.ToHttpString(request.Method);
        _accessLogger.LogAccess(_clientIp, method, request.Uri, request.Version, statusCode, bytesSent);
    }

    /// <summary>
    /// Builds an absolute URI from the request URI and host/port.
    /// Aligns with tinyproxy C's URL handling in establish_http_connection.
    /// </summary>
    private static string GetAbsoluteUri(string uri, string host, int port)
    {
        // If URI already has a scheme, use it as-is
        if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        // Build absolute URI
        // Omit port for standard ports (80 for http, 443 for https) - matches tinyproxy C behavior
        var portSuffix = (port == 80) ? "" : $":{port}";
        return $"http://{host}{portSuffix}{uri}";
    }
}
