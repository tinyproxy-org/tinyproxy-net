using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TinyProxy.Config;
using TinyProxy.Filter;
using TinyProxy.Logging;
using TinyProxy.Metrics;
using TinyProxy.Protocol.Http;
using TinyProxy.Security;

namespace TinyProxy.Core;

/// <summary>
/// Manages a single proxy connection lifecycle using System.IO.Pipelines.
/// </summary>
public sealed class Connection : IDisposable
{
    private readonly Socket _clientSocket;
    private readonly ILogger _logger;
    private readonly Configuration _config;
    private readonly Stats _stats;
    private readonly AccessLogger _accessLogger;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _clientIp;
    private readonly AccessControl _accessControl;
    private readonly BasicAuth _basicAuth;
    private readonly UrlFilter _urlFilter;
    private readonly LoopDetector _loopDetector;
    private bool _disposed;

    public Socket ClientSocket => _clientSocket;
    public string RemoteEndPoint => _clientSocket.RemoteEndPoint?.ToString() ?? "unknown";
    public string ClientIp => _clientIp;

    public Connection(
        Socket clientSocket,
        ILogger logger,
        Configuration config,
        Stats stats,
        AccessLogger accessLogger,
        LoopDetector loopDetector)
    {
        _clientSocket = clientSocket ?? throw new ArgumentNullException(nameof(clientSocket));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _accessLogger = accessLogger ?? throw new ArgumentNullException(nameof(accessLogger));
        _loopDetector = loopDetector ?? throw new ArgumentNullException(nameof(loopDetector));

        _clientIp = ExtractClientIp();

        // Create filter instances with current configuration
        _accessControl = new AccessControl(_config);
        _basicAuth = new BasicAuth(_config);
        _urlFilter = new UrlFilter(_config, _logger);
    }

    private string ExtractClientIp()
    {
        if (_clientSocket.RemoteEndPoint is System.Net.IPEndPoint ip) return ip.Address.ToString();
        return "unknown";
    }

    /// <summary>
    /// Starts processing the connection.
    /// </summary>
    public async ValueTask ProcessAsync()
    {
        var token = _cts.Token;

        try
        {
            // Aligns with tinyproxy C's loop.c: detect proxy self-loop before reading request.
            if (_loopDetector.IsLoopDetected(_clientSocket.RemoteEndPoint))
            {
                _stats.IncrementFailedRequests();
                _stats.IncrementDeniedRequests();
                await Protocol.HtmlErrorPages.BadRequestAsync(
                    _clientSocket,
                    "You tried to connect to the machine the proxy is running on",
                    token);
                return;
            }

            // Read first request to determine if it's CONNECT.
            var (firstRequest, isBadRequest) = await ReadFirstRequestAsync(token).ConfigureAwait(false);
            if (firstRequest == null)
            {
                if (isBadRequest)
                {
                    _stats.IncrementFailedRequests();
                    await Protocol.HtmlErrorPages.BadRequestAsync(
                        _clientSocket,
                        "Request has an invalid format",
                        token).ConfigureAwait(false);
                }

                return; // Connection closed or invalid
            }

            _stats.IncrementRequests();
            var isStatPageRequest = IsStatPageRequest(firstRequest);

            // Check access control (IP whitelist/blacklist)
            if (!_accessControl.IsAllowed(_clientSocket.RemoteEndPoint!))
            {
                _logger.LogWarning($"Access denied for {_clientSocket.RemoteEndPoint}");
                _stats.IncrementFailedRequests();
                _stats.IncrementDeniedRequests();
                await Protocol.HtmlErrorPages.ForbiddenAsync(
                    _clientSocket,
                    "Access denied by IP filter",
                    token);
                LogAccess(firstRequest, 403, 0);
                return;
            }

            // Check authentication
            var authHeader = GetAuthHeader(firstRequest, isStatPageRequest, out var statHostAuthFlow);
            if (!_basicAuth.Validate(authHeader))
            {
                _logger.LogWarning($"Authentication failed for {_clientSocket.RemoteEndPoint}");
                _stats.IncrementFailedRequests();
                _stats.IncrementDeniedRequests();

                if (statHostAuthFlow)
                {
                    await Protocol.HtmlErrorPages.UnauthorizedAsync(
                        _clientSocket,
                        _basicAuth.GetRealm(),
                        token);
                    LogAccess(firstRequest, 401, 0);
                }
                else
                {
                    await Protocol.HtmlErrorPages.ProxyAuthenticationRequiredAsync(
                        _clientSocket,
                        _basicAuth.GetRealm(),
                        token);
                    LogAccess(firstRequest, 407, 0);
                }

                return;
            }

            // Check URL filter
            if (!_urlFilter.IsRequestAllowed(firstRequest))
            {
                _logger.LogWarning($"URL filtered: {firstRequest.Uri}");
                _stats.IncrementFailedRequests();
                _stats.IncrementDeniedRequests();
                await Protocol.HtmlErrorPages.ForbiddenAsync(
                    _clientSocket,
                    "URL filtered by proxy policy",
                    token);
                LogAccess(firstRequest, 403, 0);
                return;
            }

            if (_config.Verbose) _logger.LogInfo($"{firstRequest.Method} {firstRequest.Uri}");

            // Check if this is a reverse proxy request
            if (_config.IsReverseProxyEnabled && _config.ReversePaths.Count > 0)
            {
                var reverseProxy = new Protocol.ReverseProxy(_logger, _config, _stats, _accessLogger, _clientIp);
                var handled = await reverseProxy.TryHandleAsync(this, firstRequest, token);
                if (handled) return;
            }

            // Check if this is a transparent proxy request
            // In transparent mode, we need to determine the destination from the socket
            if (_config.IsTransparentProxyEnabled)
            {
                var transparentProxy = new Protocol.TransparentProxy(_logger, _config);
                var dest = transparentProxy.GetTransparentDestination(_clientSocket, firstRequest);

                if (dest == null)
                {
                    _stats.IncrementFailedRequests();
                    _stats.IncrementDeniedRequests();
                    await Protocol.HtmlErrorPages.BadRequestAsync(
                        _clientSocket,
                        "Unable to determine destination in transparent proxy mode",
                        token);
                    LogAccess(firstRequest, 400, 0);
                    return;
                }

                // Rewrite the request URI with the transparent destination
                var newUri = transparentProxy.BuildAbsoluteUri(firstRequest.Uri, dest.Value.host, dest.Value.port, firstRequest.Uri);
                firstRequest = firstRequest.WithUri(newUri);

                if (_config.Verbose) _logger.LogInfo($"Transparent proxy resolved to: {firstRequest.Uri}");
            }

            // Check if this is a statistics page request
            if (!string.IsNullOrEmpty(_config.StatHost) && isStatPageRequest)
            {
                var statsHandler = new Protocol.StatsHandler(_logger, _config, _stats);
                await statsHandler.HandleStatsPageAsync(_clientSocket, token);
                LogAccess(firstRequest, 200, 0);
                return;
            }

            // Route based on method
            if (firstRequest.Method == Protocol.Http.HttpMethod.Connect)
            {
                // Handle CONNECT - use remaining data for tunnel
                var connectHandler = new Protocol.ConnectHandler(_logger, _config, _stats, _accessLogger, _clientIp, _loopDetector);
                await connectHandler.HandleConnectAsync(this, firstRequest, firstRequest.Body, token);
            }
            else
            {
                // Handle regular HTTP request
                var forwarder = new HttpForwarder(_logger, _config, _stats, _accessLogger, _clientIp, _loopDetector);
                await forwarder.ForwardAsync(this, firstRequest, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError($"Connection processing error: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the first HTTP request from the socket.
    /// Supports dynamic buffer growth for large headers.
    /// </summary>
    private async Task<(HttpRequest? request, bool badRequest)> ReadFirstRequestAsync(CancellationToken token)
    {
        const int InitialBufferSize = ProxyConstants.InitialHeaderBufferSize;
        const int MaxHeaderSize = ProxyConstants.MaxHeaderSize;

        var buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
        var totalReceived = 0;
        var parser = new HttpRequestParser(_logger);

        try
        {
            while (totalReceived < MaxHeaderSize)
            {
                var received = await _clientSocket.ReceiveAsync(
                    buffer.AsMemory(totalReceived),
                    SocketFlags.None,
                    token).ConfigureAwait(false);

                if (received == 0) return (null, false); // Connection closed

                totalReceived += received;

                // Find end of headers (double CRLF)
                var headerEnd = FindHeaderEnd(buffer.AsSpan(0, totalReceived));
                if (headerEnd >= 0)
                {
                    // Full headers received, parse the request
                    var sequence = new ReadOnlySequence<byte>(buffer.AsMemory(0, totalReceived));

                    if (!parser.TryParseRequest(ref sequence, out var request))
                    {
                        _logger.LogWarning("Failed to parse request");
                        return (null, true);
                    }

                    if (request == null) return (null, true);

                    // The receive buffer is returned to ArrayPool in finally.
                    // Detach any pre-read body bytes to avoid use-after-return.
                    return (CloneBodyIfNeeded(request), false);
                }

                // Need more data - grow buffer if needed
                if (totalReceived >= buffer.Length)
                {
                    var newBufferSize = Math.Min(buffer.Length * 2, MaxHeaderSize);
                    if (newBufferSize <= buffer.Length)
                    {
                        _logger.LogWarning("Request headers too large");
                        return (null, true);
                    }

                    var newBuffer = ArrayPool<byte>.Shared.Rent(newBufferSize);
                    Buffer.BlockCopy(buffer, 0, newBuffer, 0, totalReceived);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = newBuffer;
                }
            }

            _logger.LogWarning("Request headers exceeded maximum size");
            return (null, true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static HttpRequest CloneBodyIfNeeded(HttpRequest request)
    {
        if (request.Body.Length == 0) return request;

        var bodyCopy = request.Body.ToArray();
        return request.WithBody(new ReadOnlySequence<byte>(bodyCopy));
    }

    /// <summary>
    /// Finds the end of HTTP headers (\r\n\r\n or \n\n).
    /// Aligns with tinyproxy C's CHECK_CRLF macro which supports both \r\n and single \n.
    /// </summary>
    private static int FindHeaderEnd(ReadOnlySpan<byte> span)
    {
        for (var i = 0; i < span.Length - 1; i++)
        {
            // Check for CRLF CRLF (standard)
            if (i < span.Length - 3 &&
                span[i] == '\r' && span[i + 1] == '\n' &&
                span[i + 2] == '\r' && span[i + 3] == '\n')
                return i + 4;
            // Check for LF LF (non-standard but allowed by tinyproxy)
            if (span[i] == '\n' && span[i + 1] == '\n') return i + 2;
        }

        return -1;
    }

    /// <summary>
    /// Checks if the request is for the statistics page.
    /// Aligns with tinyproxy C's statpage functionality in stats.c.
    /// </summary>
    private bool IsStatPageRequest(HttpRequest request)
    {
        if (string.IsNullOrEmpty(_config.StatHost)) return false;

        // Check if the Host header matches StatHost
        foreach (var kvp in request.Headers)
            if (string.Equals(kvp.Key, "Host", StringComparison.OrdinalIgnoreCase))
            {
                // Use span-based parsing to avoid allocation
                var hostSpan = kvp.Value.IsSingleSegment ? kvp.Value.FirstSpan : kvp.Value.ToArray();
                var host = System.Text.Encoding.ASCII.GetString(hostSpan);

                // Exact match or hostname match (without port)
                if (string.Equals(host, _config.StatHost, StringComparison.OrdinalIgnoreCase) ||
                    host.StartsWith(_config.StatHost + ":", StringComparison.OrdinalIgnoreCase))
                    return true;

                break;
            }

        return false;
    }

    private void LogAccess(HttpRequest request, int statusCode, long bytesSent)
    {
        var method = request.GetMethodToken();
        _accessLogger.LogAccess(_clientIp, method, request.Uri, request.Version, statusCode, bytesSent);
    }

    private static string? GetAuthHeader(HttpRequest request, bool isStatPageRequest, out bool statHostAuthFlow)
    {
        statHostAuthFlow = false;

        var proxyAuth = GetHeaderValue(request.Headers, "Proxy-Authorization");
        if (proxyAuth != null) return proxyAuth;

        if (!isStatPageRequest) return null;

        statHostAuthFlow = true;
        return GetHeaderValue(request.Headers, "Authorization");
    }

    private static string? GetHeaderValue(IDictionary<string, ReadOnlySequence<byte>> headers, string headerName)
    {
        if (!headers.TryGetValue(headerName, out var value) || value.Length == 0) return null;

        var span = value.IsSingleSegment ? value.FirstSpan : value.ToArray();
        return System.Text.Encoding.ASCII.GetString(span);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Close socket first to unblock pending operations
        _clientSocket.Dispose();

        // Then cancel and dispose token
        _cts.Cancel();
        _cts.Dispose();
    }
}
