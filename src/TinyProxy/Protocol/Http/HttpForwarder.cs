using System.Buffers.Text;

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
    private readonly LoopDetector? _loopDetector;

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpForwarder"/> class.
    /// </summary>
    public HttpForwarder(
        ILogger logger,
        Configuration config,
        Stats stats,
        AccessLogger accessLogger,
        string clientIp,
        LoopDetector? loopDetector = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _accessLogger = accessLogger ?? throw new ArgumentNullException(nameof(accessLogger));
        _clientIp = clientIp ?? "unknown";
        _loopDetector = loopDetector;
    }

    /// <summary>
    /// Executes forward async.
    /// </summary>
    public async ValueTask ForwardAsync(
        Connection connection,
        HttpRequest request,
        CancellationToken token)
    {
        var statusCode = 200;
        long bytesSent = 0;

        if (!TryResolveTarget(request, out var host, out var port, out var unsupportedProtocol))
        {
            _stats.IncrementFailedRequests();
            if (unsupportedProtocol)
            {
                statusCode = 501;
                await SendErrorAsync(
                    connection.ClientSocket,
                    501,
                    "Not Implemented",
                    "Unknown method or unsupported protocol.",
                    token).ConfigureAwait(false);
            }
            else
            {
                statusCode = 400;
                await SendErrorAsync(
                    connection.ClientSocket,
                    400,
                    "Bad Request",
                    "Invalid target host",
                    token).ConfigureAwait(false);
            }

            LogAccess(connection, request, statusCode, 0);
            return;
        }

        if (_config.MaxRequestSize > 0 && request.ContentLength.HasValue && request.ContentLength.Value > _config.MaxRequestSize)
        {
            _stats.IncrementFailedRequests();
            await SendErrorAsync(connection.ClientSocket, 413, "Payload Too Large",
                $"Request body exceeds maximum allowed size of {_config.MaxRequestSize} bytes",
                token).ConfigureAwait(false);
            LogAccess(connection, request, 413, 0);
            return;
        }

        if (_config.Verbose) _logger.LogInfo($"Forwarding {request.GetMethodToken()} {request.Uri}");

        var upstream = _config.ResolveUpstreamProxy(host);
        Socket serverSocket = null!;
        long bytesReceived = 0;
        IdleTimeoutScope? requestIdleTimeoutScope = null;

        try
        {
            if (upstream != null)
            {
                serverSocket = await ConnectViaUpstreamAsync(upstream, host, port, connection.ClientSocket, token).ConfigureAwait(false);
            }
            else
            {
                serverSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                serverSocket.BindToSameIp(connection.ClientSocket, _config);

                if (_config.BindAddresses.Count > 0)
                {
                    var bindAddress = _config.BindAddresses.FirstOrDefault();
                    if (!string.IsNullOrEmpty(bindAddress))
                    {
                        await serverSocket.ConnectAndBindAsync(
                            host, port, _config.Timeout, bindAddress, token).ConfigureAwait(false);
                        goto Connected;
                    }
                }

                await serverSocket.ConnectAsync(host, port, _config.Timeout, token).ConfigureAwait(false);
            }

            Connected:
            RecordPotentialLoopEndpoint(serverSocket, port);
            requestIdleTimeoutScope = new IdleTimeoutScope(_config.Timeout, token);
            var requestIoToken = requestIdleTimeoutScope.Token;

            var useAbsoluteUri = upstream?.Type == UpstreamProxyType.Http;
            var requestBuffer = BuildForwardRequest(request, host, port, useAbsoluteUri);
            await serverSocket.SendAllAsync(requestBuffer, requestIoToken).ConfigureAwait(false);
            requestIdleTimeoutScope.Touch();

            await ForwardRequestBodyAsync(
                connection.ClientSocket,
                serverSocket,
                request,
                requestIoToken,
                requestIdleTimeoutScope.Touch).ConfigureAwait(false);

            (bytesSent, bytesReceived) = await ForwardResponseAsync(
                serverSocket,
                connection.ClientSocket,
                request.Method,
                request.Version,
                request.ReverseMagicCookiePath,
                token).ConfigureAwait(false);

            _stats.AddBytesSent(bytesSent);
            _stats.AddBytesReceived(bytesReceived);

            serverSocket.Shutdown(SocketShutdown.Both);
        }
        catch (RequestBodyTooLargeException)
        {
            _stats.IncrementFailedRequests();
            statusCode = 413;
            await SendErrorAsync(
                connection.ClientSocket,
                413,
                "Payload Too Large",
                $"Request body exceeds maximum allowed size of {_config.MaxRequestSize} bytes",
                token).ConfigureAwait(false);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            _stats.IncrementFailedRequests();
            if (upstream == null)
            {
                statusCode = 500;
                await SendErrorAsync(
                    connection.ClientSocket,
                    500,
                    "Internal Server Error",
                    $"Could not connect to {host}:{port}",
                    token).ConfigureAwait(false);
            }
            else
            {
                statusCode = 502;
                await SendErrorAsync(
                    connection.ClientSocket,
                    502,
                    "Bad Gateway",
                    $"Could not connect to {host}:{port}",
                    token).ConfigureAwait(false);
            }
        }
        catch (ResponseForwardingTimeoutException ex)
        {
            _stats.IncrementFailedRequests();
            if (upstream == null)
            {
                statusCode = 500;
                if (!ex.ResponseStarted)
                    await SendErrorAsync(
                        connection.ClientSocket,
                        500,
                        "Internal Server Error",
                        "Server response timeout",
                        token).ConfigureAwait(false);
            }
            else
            {
                statusCode = 504;
                if (!ex.ResponseStarted)
                    await SendErrorAsync(
                        connection.ClientSocket,
                        504,
                        "Gateway Timeout",
                        "Server response timeout",
                        token).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            _stats.IncrementFailedRequests();
            if (upstream == null)
            {
                statusCode = 500;
                await SendErrorAsync(
                    connection.ClientSocket,
                    500,
                    "Internal Server Error",
                    "Server response timeout",
                    token).ConfigureAwait(false);
            }
            else
            {
                statusCode = 504;
                await SendErrorAsync(
                    connection.ClientSocket,
                    504,
                    "Gateway Timeout",
                    "Server response timeout",
                    token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (requestIdleTimeoutScope?.IsTimeoutCancellation == true)
        {
            _stats.IncrementFailedRequests();
            if (upstream == null)
            {
                statusCode = 500;
                await SendErrorAsync(
                    connection.ClientSocket,
                    500,
                    "Internal Server Error",
                    "Server response timeout",
                    token).ConfigureAwait(false);
            }
            else
            {
                statusCode = 504;
                await SendErrorAsync(
                    connection.ClientSocket,
                    504,
                    "Gateway Timeout",
                    "Server response timeout",
                    token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Server shutdown - don't send error response
            _stats.IncrementFailedRequests();
            statusCode = 504;
        }
        catch (OperationCanceledException)
        {
            // Request timeout - treat as Gateway Timeout
            _stats.IncrementFailedRequests();
            statusCode = 504;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Forward error: {ex.Message}");
            _stats.IncrementFailedRequests();
            if (upstream == null)
            {
                statusCode = 500;
                await SendErrorAsync(
                    connection.ClientSocket,
                    500,
                    "Internal Server Error",
                    ex.Message,
                    token).ConfigureAwait(false);
            }
            else
            {
                statusCode = 502;
                await SendErrorAsync(connection.ClientSocket, 502, "Bad Gateway", ex.Message, token).ConfigureAwait(false);
            }
        }
        finally
        {
            requestIdleTimeoutScope?.Dispose();

            try
            {
                serverSocket?.Dispose();
            }
            catch (SocketException)
            {
            }

            LogAccess(connection, request, statusCode, bytesSent);
        }
    }

    private async Task<Socket> ConnectViaUpstreamAsync(
        UpstreamProxyConfig upstream,
        string targetHost,
        int targetPort,
        Socket clientSocket,
        CancellationToken token)
    {
        if (upstream.Type == UpstreamProxyType.Socks4 || upstream.Type == UpstreamProxyType.Socks5)
        {
            var socksProxy = new SocksUpstreamProxy(_logger, upstream, _config.Timeout);
            return await socksProxy.ConnectAsync(targetHost, targetPort, token, clientSocket, _config).ConfigureAwait(false);
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

        socket.BindToSameIp(clientSocket, _config);
        if (_config.BindAddresses.Count > 0)
        {
            var bindAddress = _config.BindAddresses.FirstOrDefault();
            if (!string.IsNullOrEmpty(bindAddress))
            {
                await socket.ConnectAndBindAsync(
                    upstream.Host,
                    upstream.Port,
                    _config.Timeout,
                    bindAddress,
                    token).ConfigureAwait(false);
                RecordPotentialLoopEndpoint(socket, upstream.Port);
                return socket;
            }
        }

        await socket.ConnectAsync(upstream.Host, upstream.Port, _config.Timeout, token).ConfigureAwait(false);
        RecordPotentialLoopEndpoint(socket, upstream.Port);
        return socket;
    }

    private void RecordPotentialLoopEndpoint(Socket socket, int destinationPort)
    {
        if (_loopDetector == null) return;
        if (destinationPort != _config.ListenPort) return;
        _loopDetector.RecordOutgoingLocalEndpoint(socket.LocalEndPoint);
    }

    private byte[] BuildForwardRequest(HttpRequest request, string host, int port, bool useAbsoluteUri)
    {
        // PERF: StringBuilder avoids repeated string allocations when composing headers.
        var sb = StringBuilderCache.Acquire();
        var upstream = _config.ResolveUpstreamProxy(host);

        try
        {
            // Request line - direct/origin uses origin-form; HTTP upstream uses absolute-form.
            var method = request.GetMethodToken();
            var requestTarget = GetForwardRequestTarget(request.Uri, host, port, useAbsoluteUri);
            var outboundVersion = NormalizeOutboundHttpVersion(request.Version);

            sb.Append(method).Append(' ').Append(requestTarget).Append(' ').Append(outboundVersion);
            sb.Append(ProxyConstants.Crlf);
            sb.Append("Host: ").Append(FormatHostHeader(host, port)).Append(ProxyConstants.Crlf);

            var hopByHopHeaders = ProxyConstants.HopByHopHeadersSet;
            var connectionTokenHeaders = ExtractConnectionTokenHeaders(request);
            string? upstreamViaHeader = null;
            var anonymousFilter = new AnonymousFilter(_config.AnonymousAllowedHeaders);

            void AppendFilteredHeader(string name, ReadOnlySequence<byte> value)
            {
                // Skip hop-by-hop headers
                // Keep Transfer-Encoding for request-body semantics (e.g. chunked).
                if (!string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                    hopByHopHeaders.Contains(name))
                    return;

                // Remove headers listed in Connection/Proxy-Connection options.
                if (connectionTokenHeaders.Contains(name))
                    return;

                // Rebuild Host from parsed target to keep a single canonical value.
                if (string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase))
                    return;

                // We append our own Via later to preserve proxy chain semantics.
                if (_config.AddViaHeader && string.Equals(name, "Via", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(upstreamViaHeader))
                        upstreamViaHeader = ReadHeaderValue(value);
                    return;
                }


                if (_config.IsAnonymousEnabled && !anonymousFilter.IsHeaderAllowed(name)) return;

                sb.Append(name).Append(": ");
                sb.Append(ReadHeaderValue(value));
                sb.Append(ProxyConstants.Crlf);
            }

            if (request.HeaderLines.Count > 0)
                foreach (var header in request.HeaderLines)
                    AppendFilteredHeader(header.Key, header.Value);
            else
                foreach (var header in request.Headers)
                    AppendFilteredHeader(header.Key, header.Value);

            // Only emit Proxy-Authorization for HTTP upstream forwarding.
            if (useAbsoluteUri &&
                upstream?.Type == UpstreamProxyType.Http &&
                !string.IsNullOrEmpty(upstream.Username))
            {
                var credentials = $"{upstream.Username}:{upstream.Password}";
                var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes(credentials));
                sb.Append("Proxy-Authorization: Basic ").Append(encoded);
                sb.Append(ProxyConstants.Crlf);
            }

            if (_config.AddViaHeader)
            {
                var viaName = string.IsNullOrWhiteSpace(_config.ViaProxyName)
                    ? Environment.MachineName
                    : _config.ViaProxyName!;
                if (string.IsNullOrWhiteSpace(viaName)) viaName = "unknown";
                var viaProtocolToken = GetViaProtocolToken(request.Version);

                sb.Append("Via: ");
                if (!string.IsNullOrWhiteSpace(upstreamViaHeader)) sb.Append(upstreamViaHeader).Append(", ");
                sb.Append(viaProtocolToken).Append(' ').Append(viaName).Append(ProxyConstants.Crlf);
            }

            sb.Append("Connection: close").Append(ProxyConstants.Crlf);
            if (_config.AddXTinyproxyHeader) sb.Append("X-Tinyproxy: ").Append(_clientIp).Append(ProxyConstants.Crlf);
            foreach (var header in _config.CustomHeaders)
            {
                if (connectionTokenHeaders.Contains(header.Name))
                    continue;

                if (hopByHopHeaders.Contains(header.Name) ||
                    string.Equals(header.Name, "Host", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (_config.IsAnonymousEnabled && !anonymousFilter.IsHeaderAllowed(header.Name))
                    continue;

                sb.Append(header.Name).Append(": ").Append(header.Value);
                sb.Append(ProxyConstants.Crlf);
            }

            sb.Append(ProxyConstants.Crlf);

            return Encoding.ASCII.GetBytes(StringBuilderCache.GetStringAndRelease(sb));
        }
        catch
        {
            StringBuilderCache.Release(sb);
            throw;
        }
    }

    private static async ValueTask<long> SendBodyAsync(
        Socket socket,
        ReadOnlySequence<byte> body,
        long maxBytes,
        CancellationToken token,
        Action? onActivity = null)
    {
        if (maxBytes <= 0 || body.Length == 0) return 0;

        long sent = 0;
        foreach (var segment in body)
        {
            if (sent >= maxBytes) break;

            var remaining = maxBytes - sent;
            var toSend = (int)Math.Min(segment.Length, remaining);
            if (toSend <= 0) break;

            await socket.SendAllAsync(segment.Slice(0, toSend), token).ConfigureAwait(false);
            onActivity?.Invoke();
            sent += toSend;
        }

        return sent;
    }

    private static string NormalizeOutboundHttpVersion(string? requestVersion)
    {
        if (TryParseHttpVersion(requestVersion, out var major, out var minor) && major == 1)
            return $"HTTP/1.{minor}";

        return "HTTP/1.0";
    }

    private static string GetViaProtocolToken(string? requestVersion)
    {
        if (TryParseHttpVersion(requestVersion, out var major, out var minor))
            return $"{major}.{minor}";

        return "1.0";
    }

    private static bool IsHttp09Request(string? requestVersion)
    {
        return TryParseHttpVersion(requestVersion, out var major, out var minor) &&
               major == 0 &&
               minor == 9;
    }

    private static bool TryParseHttpVersion(string? requestVersion, out int major, out int minor)
    {
        major = 0;
        minor = 0;

        if (string.IsNullOrWhiteSpace(requestVersion)) return false;
        if (!requestVersion.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)) return false;

        var versionPart = requestVersion.AsSpan(5);
        var dotIndex = versionPart.IndexOf('.');
        if (dotIndex <= 0 || dotIndex >= versionPart.Length - 1) return false;

        return TryParseUnsignedPrefix(versionPart[..dotIndex], true, out major) &&
               TryParseUnsignedPrefix(versionPart[(dotIndex + 1)..], false, out minor);
    }

    // Parse an unsigned numeric prefix; optionally allow a trailing suffix.
    private static bool TryParseUnsignedPrefix(ReadOnlySpan<char> value, bool requireWholeToken, out int number)
    {
        number = 0;
        if (value.IsEmpty) return false;

        var consumed = 0;
        while (consumed < value.Length)
        {
            var current = value[consumed];
            if (current < '0' || current > '9') break;

            var digit = current - '0';
            if (number > (int.MaxValue - digit) / 10) return false;

            number = number * 10 + digit;
            consumed++;
        }

        if (consumed == 0) return false;
        if (requireWholeToken && consumed != value.Length) return false;

        return true;
    }

    private async ValueTask ForwardRequestBodyAsync(
        Socket clientSocket,
        Socket serverSocket,
        HttpRequest request,
        CancellationToken token,
        Action? onActivity = null)
    {
        // Prefer Content-Length framing when present; otherwise process chunked body.
        if (request.ContentLength.HasValue)
        {
            if (request.ContentLength.Value <= 0) return;

            var contentLength = request.ContentLength.Value;
            var sentFromBuffer = await SendBodyAsync(serverSocket, request.Body, contentLength, token, onActivity).ConfigureAwait(false);
            var remainingBytes = contentLength - sentFromBuffer;

            if (remainingBytes <= 0) return;

            await RelayRequestBodyAsync(
                clientSocket,
                serverSocket,
                remainingBytes,
                token,
                onActivity).ConfigureAwait(false);
            return;
        }

        if (HasChunkedTransferEncoding(request))
            await ForwardChunkedBodyAsync(clientSocket, serverSocket, request.Body, token, onActivity).ConfigureAwait(false);
    }

    private async ValueTask ForwardChunkedBodyAsync(
        Socket clientSocket,
        Socket serverSocket,
        ReadOnlySequence<byte> prefetchedBody,
        CancellationToken token,
        Action? onActivity = null)
    {
        var reader = new PrebufferedSocketReader(clientSocket, prefetchedBody);
        var lineBuffer = ArrayPool<byte>.Shared.Rent(ProxyConstants.InitialHeaderBufferSize);
        var copyBuffer = ArrayPool<byte>.Shared.Rent(ProxyConstants.StreamBufferSize);
        long totalPayloadBytes = 0;

        try
        {
            while (true)
            {
                var lineLength = await ReadLineAsync(reader, lineBuffer, token, onActivity).ConfigureAwait(false);
                await serverSocket.SendAllAsync(lineBuffer.AsMemory(0, lineLength), token).ConfigureAwait(false);
                onActivity?.Invoke();

                var chunkSize = ParseChunkSize(lineBuffer.AsMemory(0, lineLength));
                if (chunkSize == 0)
                {
                    await ForwardChunkTrailersAsync(reader, serverSocket, lineBuffer, token, onActivity).ConfigureAwait(false);
                    break;
                }

                totalPayloadBytes += chunkSize;
                if (_config.MaxRequestSize > 0 && totalPayloadBytes > _config.MaxRequestSize)
                    throw new RequestBodyTooLargeException();

                await CopyExactlyAsync(reader, serverSocket, copyBuffer, chunkSize, token, onActivity).ConfigureAwait(false);

                var chunkTerminatorLength = await ReadLineAsync(reader, lineBuffer, token, onActivity).ConfigureAwait(false);
                if (!IsEmptyLine(lineBuffer, chunkTerminatorLength))
                    throw new InvalidOperationException("Invalid chunk terminator.");
                await serverSocket.SendAllAsync(lineBuffer.AsMemory(0, chunkTerminatorLength), token).ConfigureAwait(false);
                onActivity?.Invoke();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(lineBuffer);
            ArrayPool<byte>.Shared.Return(copyBuffer);
        }
    }

    private static bool HasChunkedTransferEncoding(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Transfer-Encoding", out var value)) return false;

        var span = value.IsSingleSegment ? value.FirstSpan : value.ToArray();
        return TextUtils.IndexOfIgnoreCase(span, "chunked"u8) >= 0;
    }

    private static async ValueTask<int> ReadLineAsync(
        PrebufferedSocketReader reader,
        byte[] lineBuffer,
        CancellationToken token,
        Action? onActivity = null)
    {
        var length = 0;
        while (length < lineBuffer.Length)
        {
            var read = await reader.ReadAsync(lineBuffer.AsMemory(length, 1), token).ConfigureAwait(false);
            if (read == 0) throw new InvalidOperationException("Connection closed while reading chunked body.");
            onActivity?.Invoke();
            length += read;

            if (lineBuffer[length - 1] == (byte)'\n')
                return length;
        }

        throw new InvalidOperationException("Chunked body line exceeds maximum supported length.");
    }

    private static long ParseChunkSize(ReadOnlyMemory<byte> lineWithCrlf)
    {
        var lineSpan = lineWithCrlf.Span;

        if (lineSpan.Length < 1 || lineSpan[^1] != (byte)'\n')
            throw new InvalidOperationException("Invalid chunk-size line terminator.");

        var line = lineSpan[..^1];
        if (!line.IsEmpty && line[^1] == (byte)'\r') line = line[..^1];
        var extensionIndex = line.IndexOf((byte)';');
        if (extensionIndex >= 0) line = line[..extensionIndex];
        line = TextUtils.Trim(line);
        if (line.IsEmpty) throw new InvalidOperationException("Empty chunk-size line.");

        if (!Utf8Parser.TryParse(line, out long chunkSize, out var consumed, 'X') ||
            consumed != line.Length ||
            chunkSize < 0)
            throw new InvalidOperationException("Invalid chunk-size value.");

        return chunkSize;
    }

    private static async ValueTask ForwardChunkTrailersAsync(
        PrebufferedSocketReader reader,
        Socket serverSocket,
        byte[] lineBuffer,
        CancellationToken token,
        Action? onActivity = null)
    {
        while (true)
        {
            var lineLength = await ReadLineAsync(reader, lineBuffer, token, onActivity).ConfigureAwait(false);
            await serverSocket.SendAllAsync(lineBuffer.AsMemory(0, lineLength), token).ConfigureAwait(false);
            onActivity?.Invoke();

            if (IsEmptyLine(lineBuffer, lineLength))
                return;
        }
    }

    private static bool IsEmptyLine(byte[] lineBuffer, int lineLength)
    {
        return (lineLength == 2 &&
                lineBuffer[0] == (byte)'\r' &&
                lineBuffer[1] == (byte)'\n') ||
               (lineLength == 1 && lineBuffer[0] == (byte)'\n');
    }

    private static async ValueTask CopyExactlyAsync(
        PrebufferedSocketReader reader,
        Socket destination,
        byte[] buffer,
        long bytesToCopy,
        CancellationToken token,
        Action? onActivity = null)
    {
        while (bytesToCopy > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, bytesToCopy);
            var read = await reader.ReadAsync(buffer.AsMemory(0, toRead), token).ConfigureAwait(false);
            if (read == 0) throw new InvalidOperationException("Connection closed while forwarding chunked body.");
            onActivity?.Invoke();

            await destination.SendAllAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            onActivity?.Invoke();
            bytesToCopy -= read;
        }
    }

    private static async ValueTask RelayRequestBodyAsync(
        Socket clientSocket,
        Socket serverSocket,
        long remainingBytes,
        CancellationToken token,
        Action? onActivity = null)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ProxyConstants.StreamBufferSize);
        try
        {
            while (remainingBytes > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remainingBytes);
                var received = await clientSocket.ReceiveAsync(
                    buffer.AsMemory(0, toRead),
                    SocketFlags.None,
                    token).ConfigureAwait(false);

                if (received == 0) throw new InvalidOperationException("Client closed connection before sending complete request body.");
                onActivity?.Invoke();

                await serverSocket.SendAllAsync(
                    buffer.AsMemory(0, received),
                    token).ConfigureAwait(false);
                onActivity?.Invoke();

                remainingBytes -= received;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Forwards response data from server to client.
    /// </summary>
    private async Task<(long sent, long received)> ForwardResponseAsync(
        Socket server,
        Socket client,
        HttpMethod requestMethod,
        string requestVersion,
        string? reverseMagicCookiePath,
        CancellationToken token)
    {
        using var idleTimeoutScope = new IdleTimeoutScope(_config.Timeout, token);
        var headerBuffer = ArrayPool<byte>.Shared.Rent(ProxyConstants.InitialHeaderBufferSize);
        long totalSent = 0;
        long totalReceived = 0;
        var pendingPrefetched = ReadOnlySequence<byte>.Empty;
        var interimResponsesForwarded = 0;
        var viaProtocolToken = GetViaProtocolToken(requestVersion);
        var omitResponseHeaders = IsHttp09Request(requestVersion);
        var responseIoToken = idleTimeoutScope.Token;

        try
        {
            while (true)
            {
                var headerBuffered = 0;
                var headerEnd = -1;
                var headerReader = new PrebufferedSocketReader(server, pendingPrefetched);

                while (headerEnd < 0)
                {
                    if (headerBuffered == headerBuffer.Length)
                    {
                        if (headerBuffer.Length >= ProxyConstants.MaxHeaderSize)
                            throw new InvalidOperationException("Response headers too large.");

                        var newSize = Math.Min(headerBuffer.Length * 2, ProxyConstants.MaxHeaderSize);
                        var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
                        Buffer.BlockCopy(headerBuffer, 0, newBuffer, 0, headerBuffered);
                        ArrayPool<byte>.Shared.Return(headerBuffer);
                        headerBuffer = newBuffer;
                    }

                    var received = await headerReader.ReadAsync(
                        headerBuffer.AsMemory(headerBuffered),
                        responseIoToken).ConfigureAwait(false);
                    if (received == 0) throw new InvalidOperationException("Connection closed while reading response headers.");
                    idleTimeoutScope.Touch();

                    headerBuffered += received;

                    if (TryFindHeadersEnd(headerBuffer.AsSpan(0, headerBuffered), out var foundHeaderEnd))
                        headerEnd = foundHeaderEnd;
                }

                totalReceived += headerReader.SocketBytesRead;

                var (statusCode, isChunked, contentLength) = ParseResponseHeaderInfo(
                    headerBuffer.AsMemory(0, headerEnd));
                var sanitizedHeader = BuildForwardResponseHeader(
                    headerBuffer.AsSpan(0, headerEnd),
                    statusCode,
                    _config.AddViaHeader,
                    _config.ViaProxyName,
                    viaProtocolToken,
                    _config.ReverseBaseUrl,
                    _config.ReversePaths,
                    _config.ReverseMagicEnabled ? reverseMagicCookiePath : null);
                if (!omitResponseHeaders)
                {
                    await client.SendAllAsync(sanitizedHeader, responseIoToken).ConfigureAwait(false);
                    idleTimeoutScope.Touch();
                    totalSent += sanitizedHeader.Length;
                }

                var prefetchedBodyLength = headerBuffered - headerEnd;
                var prefetchedBody = prefetchedBodyLength > 0
                    ? new ReadOnlySequence<byte>(headerBuffer.AsMemory(headerEnd, prefetchedBodyLength))
                    : ReadOnlySequence<byte>.Empty;

                if (IsInterimResponseStatusCode(statusCode))
                {
                    interimResponsesForwarded++;
                    if (interimResponsesForwarded > 8)
                        throw new InvalidOperationException("Too many interim responses from upstream.");

                    pendingPrefetched = prefetchedBodyLength > 0
                        ? new ReadOnlySequence<byte>(prefetchedBody.ToArray())
                        : ReadOnlySequence<byte>.Empty;
                    continue;
                }

                var bodyMode = DetermineResponseBodyMode(requestMethod, statusCode, isChunked, contentLength);
                var reader = new PrebufferedSocketReader(server, prefetchedBody);

                switch (bodyMode)
                {
                    case ResponseBodyMode.None:
                        if (!prefetchedBody.IsEmpty)
                        {
                            await client.SendAllAsync(prefetchedBody, responseIoToken).ConfigureAwait(false);
                            idleTimeoutScope.Touch();
                            totalSent += prefetchedBody.Length;
                        }

                        break;
                    case ResponseBodyMode.ContentLength:
                    {
                        var buffer = ArrayPool<byte>.Shared.Rent(ProxyConstants.DefaultBufferSize);
                        try
                        {
                            totalSent += await ForwardFixedLengthBodyAsync(
                                reader,
                                client,
                                buffer,
                                contentLength ?? 0,
                                responseIoToken,
                                idleTimeoutScope.Touch).ConfigureAwait(false);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }

                        break;
                    }
                    case ResponseBodyMode.Chunked:
                    {
                        var lineBuffer = ArrayPool<byte>.Shared.Rent(ProxyConstants.InitialHeaderBufferSize);
                        var copyBuffer = ArrayPool<byte>.Shared.Rent(ProxyConstants.StreamBufferSize);
                        try
                        {
                            totalSent += await ForwardChunkedStreamAsync(
                                reader,
                                client,
                                lineBuffer,
                                copyBuffer,
                                responseIoToken,
                                idleTimeoutScope.Touch).ConfigureAwait(false);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(lineBuffer);
                            ArrayPool<byte>.Shared.Return(copyBuffer);
                        }

                        break;
                    }
                    case ResponseBodyMode.UntilClose:
                    {
                        var buffer = ArrayPool<byte>.Shared.Rent(ProxyConstants.DefaultBufferSize);
                        try
                        {
                            totalSent += await ForwardUntilCloseAsync(
                                reader,
                                client,
                                buffer,
                                responseIoToken,
                                idleTimeoutScope.Touch).ConfigureAwait(false);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }

                        break;
                    }
                    case ResponseBodyMode.UpgradedTunnel:
                    {
                        var serverToClientBuffer = ArrayPool<byte>.Shared.Rent(ProxyConstants.DefaultBufferSize);
                        var clientToServerBuffer = ArrayPool<byte>.Shared.Rent(ProxyConstants.DefaultBufferSize);
                        try
                        {
                            totalSent += await ForwardUpgradedTunnelAsync(
                                reader,
                                server,
                                client,
                                serverToClientBuffer,
                                clientToServerBuffer,
                                token).ConfigureAwait(false);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(serverToClientBuffer);
                            ArrayPool<byte>.Shared.Return(clientToServerBuffer);
                        }

                        break;
                    }
                    default:
                        throw new InvalidOperationException("Unknown response body mode.");
                }

                totalReceived += reader.SocketBytesRead;
                return (totalSent, totalReceived);
            }
        }
        catch (OperationCanceledException) when (idleTimeoutScope.IsTimeoutCancellation)
        {
            throw new ResponseForwardingTimeoutException(totalSent > 0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer);
        }
    }

    private async Task<long> ForwardUpgradedTunnelAsync(
        PrebufferedSocketReader serverReader,
        Socket server,
        Socket client,
        byte[] serverToClientBuffer,
        byte[] clientToServerBuffer,
        CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var idleTimeout = _config.ConnectIdleTimeout;
        var idleTimeoutSync = idleTimeout > TimeSpan.Zero ? new object() : null;

        void TouchIdleTimeout()
        {
            if (idleTimeout <= TimeSpan.Zero || idleTimeoutSync == null) return;

            lock (idleTimeoutSync)
            {
                if (!cts.IsCancellationRequested)
                    cts.CancelAfter(idleTimeout);
            }
        }

        TouchIdleTimeout();

        var serverToClient = ForwardUntilCloseAsync(
            serverReader,
            client,
            serverToClientBuffer,
            cts.Token,
            TouchIdleTimeout).AsTask();
        var clientToServer = ForwardSocketUntilCloseAsync(
            client,
            server,
            clientToServerBuffer,
            cts.Token,
            TouchIdleTimeout).AsTask();

        try
        {
            await Task.WhenAll(serverToClient, clientToServer).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // Ignore idle-timeout cancellation for upgraded relay.
        }
        catch (SocketException) when (!token.IsCancellationRequested)
        {
            // Ignore peer relay socket shutdown races during upgrade teardown.
        }
        catch (ObjectDisposedException) when (!token.IsCancellationRequested)
        {
            // Ignore peer relay socket disposal races during upgrade teardown.
        }

        try
        {
            server.Shutdown(SocketShutdown.Send);
        }
        catch
        {
            // Ignore shutdown errors for closed sockets.
        }

        try
        {
            client.Shutdown(SocketShutdown.Send);
        }
        catch
        {
            // Ignore shutdown errors for closed sockets.
        }

        if (serverToClient.Status == TaskStatus.RanToCompletion)
            return serverToClient.Result;

        return 0;
    }

    private static async ValueTask<long> ForwardFixedLengthBodyAsync(
        PrebufferedSocketReader reader,
        Socket destination,
        byte[] buffer,
        long contentLength,
        CancellationToken token,
        Action? onActivity = null)
    {
        if (contentLength <= 0) return 0;

        long totalSent = 0;
        var remaining = contentLength;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await reader.ReadAsync(buffer.AsMemory(0, toRead), token).ConfigureAwait(false);
            if (read == 0) throw new InvalidOperationException("Connection closed before full response body was received.");
            onActivity?.Invoke();

            await destination.SendAllAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            onActivity?.Invoke();
            totalSent += read;
            remaining -= read;
        }

        return totalSent;
    }

    private static async ValueTask<long> ForwardChunkedStreamAsync(
        PrebufferedSocketReader reader,
        Socket destination,
        byte[] lineBuffer,
        byte[] copyBuffer,
        CancellationToken token,
        Action? onActivity = null)
    {
        long totalSent = 0;

        while (true)
        {
            var chunkSizeLineLength = await ReadLineAsync(reader, lineBuffer, token, onActivity).ConfigureAwait(false);
            await destination.SendAllAsync(lineBuffer.AsMemory(0, chunkSizeLineLength), token).ConfigureAwait(false);
            onActivity?.Invoke();
            totalSent += chunkSizeLineLength;

            var chunkSize = ParseChunkSize(lineBuffer.AsMemory(0, chunkSizeLineLength));
            if (chunkSize == 0)
                while (true)
                {
                    var trailerLength = await ReadLineAsync(reader, lineBuffer, token, onActivity).ConfigureAwait(false);
                    await destination.SendAllAsync(lineBuffer.AsMemory(0, trailerLength), token).ConfigureAwait(false);
                    onActivity?.Invoke();
                    totalSent += trailerLength;
                    if (IsEmptyLine(lineBuffer, trailerLength)) return totalSent;
                }

            await CopyExactlyAsync(reader, destination, copyBuffer, chunkSize, token, onActivity).ConfigureAwait(false);
            totalSent += chunkSize;

            var chunkTerminatorLength = await ReadLineAsync(reader, lineBuffer, token, onActivity).ConfigureAwait(false);
            if (!IsEmptyLine(lineBuffer, chunkTerminatorLength))
                throw new InvalidOperationException("Invalid chunk terminator.");
            await destination.SendAllAsync(lineBuffer.AsMemory(0, chunkTerminatorLength), token).ConfigureAwait(false);
            onActivity?.Invoke();
            totalSent += chunkTerminatorLength;
        }
    }

    private static async ValueTask<long> ForwardUntilCloseAsync(
        PrebufferedSocketReader reader,
        Socket destination,
        byte[] buffer,
        CancellationToken token,
        Action? onActivity = null)
    {
        long totalSent = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0) break;

            await destination.SendAllAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            totalSent += read;
            onActivity?.Invoke();

            if (read > ProxyConstants.YieldThreshold) await Task.Yield();
        }

        return totalSent;
    }

    private static async ValueTask<long> ForwardSocketUntilCloseAsync(
        Socket source,
        Socket destination,
        byte[] buffer,
        CancellationToken token,
        Action? onActivity = null)
    {
        long totalSent = 0;
        while (true)
        {
            var read = await source.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, token).ConfigureAwait(false);
            if (read == 0) break;

            await destination.SendAllAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            totalSent += read;
            onActivity?.Invoke();

            if (read > ProxyConstants.YieldThreshold) await Task.Yield();
        }

        return totalSent;
    }

    private static bool TryFindHeadersEnd(ReadOnlySpan<byte> bytes, out int headerEnd)
    {
        headerEnd = -1;
        if (bytes.Length < 2) return false;

        byte p3 = 0, p2 = 0, p1 = 0;
        var seenNonLineBreakByte = false;
        for (var i = 0; i < bytes.Length; i++)
        {
            var current = bytes[i];
            if (current != (byte)'\r' && current != (byte)'\n')
                seenNonLineBreakByte = true;

            if (seenNonLineBreakByte && p1 == (byte)'\n' && current == (byte)'\n')
            {
                headerEnd = i + 1;
                return true;
            }

            if (seenNonLineBreakByte &&
                p3 == (byte)'\r' && p2 == (byte)'\n' && p1 == (byte)'\r' && current == (byte)'\n')
            {
                headerEnd = i + 1;
                return true;
            }

            p3 = p2;
            p2 = p1;
            p1 = current;
        }

        return false;
    }

    private static (int statusCode, bool isChunked, long? contentLength) ParseResponseHeaderInfo(ReadOnlyMemory<byte> headerBytes)
    {
        var span = SkipLeadingBlankLines(headerBytes.Span);
        if (span.IsEmpty) throw new InvalidOperationException("Invalid response headers.");

        var firstLineEnd = span.IndexOf((byte)'\n');
        if (firstLineEnd < 0) throw new InvalidOperationException("Invalid response headers.");

        var statusLine = span[..firstLineEnd];
        if (!statusLine.IsEmpty && statusLine[^1] == (byte)'\r') statusLine = statusLine[..^1];
        if (!TryParseStatusCode(statusLine, out var statusCode))
            throw new InvalidOperationException("Invalid HTTP status line.");

        var isChunked = false;
        long? contentLength = null;
        var activeHeader = ParsedResponseHeader.None;
        var doubleCgiDetected = false;

        var offset = firstLineEnd + 1;
        while (offset < span.Length)
        {
            var lineEndRelative = span[offset..].IndexOf((byte)'\n');
            if (lineEndRelative < 0) break;

            var lineEnd = offset + lineEndRelative;
            var line = span[offset..lineEnd];
            if (!line.IsEmpty && line[^1] == (byte)'\r') line = line[..^1];

            if (line.IsEmpty) break;

            if (LooksLikeHttpStatusLine(line))
            {
                doubleCgiDetected = true;
                activeHeader = ParsedResponseHeader.None;
                offset = lineEnd + 1;
                continue;
            }

            if (doubleCgiDetected)
            {
                activeHeader = ParsedResponseHeader.None;
                offset = lineEnd + 1;
                continue;
            }

            if (IsHeaderContinuationLine(line))
            {
                var continuation = TextUtils.Trim(line);
                if (activeHeader == ParsedResponseHeader.TransferEncoding)
                    isChunked = TextUtils.IndexOfIgnoreCase(continuation, "chunked"u8) >= 0 || isChunked;
                else if (activeHeader == ParsedResponseHeader.ContentLength &&
                         !contentLength.HasValue &&
                         TryParseNonNegativeContentLength(continuation, out var continuedLength))
                    contentLength = continuedLength;

                offset = lineEnd + 1;
                continue;
            }

            var colonIndex = line.IndexOf((byte)':');
            if (colonIndex > 0)
            {
                var name = TextUtils.Trim(line[..colonIndex]);
                var value = TextUtils.Trim(line[(colonIndex + 1)..]);

                if (HeaderNameEquals(name, "Transfer-Encoding"u8))
                {
                    activeHeader = ParsedResponseHeader.TransferEncoding;
                    isChunked = TextUtils.IndexOfIgnoreCase(value, "chunked"u8) >= 0;
                }
                else if (HeaderNameEquals(name, "Content-Length"u8))
                {
                    activeHeader = ParsedResponseHeader.ContentLength;
                    if (TryParseNonNegativeContentLength(value, out var parsedLength))
                        contentLength = parsedLength;
                }
                else
                {
                    activeHeader = ParsedResponseHeader.None;
                }
            }
            else
            {
                activeHeader = ParsedResponseHeader.None;
            }

            offset = lineEnd + 1;
        }

        return (statusCode, isChunked, contentLength);
    }

    private static bool LooksLikeHttpStatusLine(ReadOnlySpan<byte> line)
    {
        if (line.Length < 5) return false;
        return EqualsIgnoreCaseAscii(line[0], (byte)'H') &&
               EqualsIgnoreCaseAscii(line[1], (byte)'T') &&
               EqualsIgnoreCaseAscii(line[2], (byte)'T') &&
               EqualsIgnoreCaseAscii(line[3], (byte)'P') &&
               line[4] == (byte)'/';
    }

    private static bool TryParseNonNegativeContentLength(ReadOnlySpan<byte> value, out long contentLength)
    {
        contentLength = 0;
        return Utf8Parser.TryParse(value, out contentLength, out var consumed) &&
               consumed == value.Length &&
               contentLength >= 0;
    }

    private enum ParsedResponseHeader : byte
    {
        None = 0,
        TransferEncoding = 1,
        ContentLength = 2
    }

    private static bool TryParseStatusCode(ReadOnlySpan<byte> statusLine, out int statusCode)
    {
        statusCode = 0;
        var firstSpace = statusLine.IndexOf((byte)' ');
        if (firstSpace < 0 || firstSpace + 1 >= statusLine.Length) return false;

        var remainder = statusLine[(firstSpace + 1)..];
        var secondSpace = remainder.IndexOf((byte)' ');
        var codeSpan = secondSpace >= 0 ? remainder[..secondSpace] : remainder;
        return Utf8Parser.TryParse(codeSpan, out statusCode, out var consumed) &&
               consumed == codeSpan.Length;
    }

    private static bool HeaderNameEquals(ReadOnlySpan<byte> name, ReadOnlySpan<byte> expected)
    {
        if (name.Length != expected.Length) return false;

        for (var i = 0; i < name.Length; i++)
            if (!EqualsIgnoreCaseAscii(name[i], expected[i]))
                return false;

        return true;
    }

    private static bool EqualsIgnoreCaseAscii(byte left, byte right)
    {
        if (left == right) return true;

        if (left is >= (byte)'A' and <= (byte)'Z') left = (byte)(left + 32);
        if (right is >= (byte)'A' and <= (byte)'Z') right = (byte)(right + 32);

        return left == right;
    }

    internal static byte[] BuildForwardResponseHeader(
        ReadOnlySpan<byte> headerSpan,
        int statusCode,
        bool addViaHeader,
        string? viaProxyName,
        string viaProtocolToken,
        string? reverseBaseUrl,
        IReadOnlyList<ReversePathConfig> reversePaths,
        string? reverseMagicCookiePath)
    {
        // Preserve 101 response headers to avoid breaking protocol upgrades.
        if (statusCode == 101) return headerSpan.ToArray();

        try
        {
            var lines = ParseHeaderLines(headerSpan, out var statusLine);
            var connectionTokenHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, value) in lines)
                if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase))
                    AddConnectionTokenHeaders(value.AsSpan(), connectionTokenHeaders);

            var sb = StringBuilderCache.Acquire(Math.Max(256, headerSpan.Length));
            try
            {
                sb.Append(statusLine).Append(ProxyConstants.Crlf);
                string? upstreamViaHeader = null;

                foreach (var (name, value) in lines)
                {
                    if (addViaHeader && name.Equals("Via", StringComparison.OrdinalIgnoreCase))
                    {
                        upstreamViaHeader = string.IsNullOrWhiteSpace(upstreamViaHeader)
                            ? value
                            : $"{upstreamViaHeader}, {value}";
                        continue;
                    }

                    if (ShouldSkipResponseHeader(name, connectionTokenHeaders)) continue;

                    if (!string.IsNullOrEmpty(reverseBaseUrl) &&
                        name.Equals("Location", StringComparison.OrdinalIgnoreCase) &&
                        TryRewriteLocationForReverseProxy(value, reverseBaseUrl!, reversePaths, out var rewrittenLocation))
                    {
                        sb.Append("Location: ").Append(rewrittenLocation).Append(ProxyConstants.Crlf);
                        continue;
                    }

                    sb.Append(name).Append(": ").Append(value).Append(ProxyConstants.Crlf);
                }

                if (addViaHeader)
                {
                    var proxyName = string.IsNullOrWhiteSpace(viaProxyName)
                        ? Environment.MachineName
                        : viaProxyName!;
                    if (string.IsNullOrWhiteSpace(proxyName)) proxyName = "unknown";

                    sb.Append("Via: ");
                    if (!string.IsNullOrWhiteSpace(upstreamViaHeader)) sb.Append(upstreamViaHeader).Append(", ");
                    sb.Append(viaProtocolToken).Append(' ').Append(proxyName).Append(ProxyConstants.Crlf);
                }

                if (!string.IsNullOrEmpty(reverseMagicCookiePath))
                    sb.Append("Set-Cookie: yummy_magical_cookie=")
                        .Append(reverseMagicCookiePath)
                        .Append("; path=/")
                        .Append(ProxyConstants.Crlf);

                sb.Append(ProxyConstants.Crlf);
                return Encoding.ASCII.GetBytes(StringBuilderCache.GetStringAndRelease(sb));
            }
            catch
            {
                StringBuilderCache.Release(sb);
                throw;
            }
        }
        catch
        {
            // Fallback to original header if rewriting fails.
            return headerSpan.ToArray();
        }
    }

    private static bool ShouldSkipResponseHeader(string headerName, HashSet<string> connectionTokenHeaders)
    {
        if (connectionTokenHeaders.Contains(headerName)) return true;

        return headerName.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryRewriteLocationForReverseProxy(
        string locationValue,
        string reverseBaseUrl,
        IReadOnlyList<ReversePathConfig> reversePaths,
        out string rewrittenLocation)
    {
        rewrittenLocation = string.Empty;
        if (reversePaths.Count == 0) return false;

        foreach (var reversePath in reversePaths)
        {
            var upstreamPrefix = reversePath.Url;
            if (!locationValue.StartsWith(upstreamPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var localPath = reversePath.Path.Length > 0 ? reversePath.Path[1..] : string.Empty;
            rewrittenLocation = string.Concat(reverseBaseUrl, localPath, locationValue[upstreamPrefix.Length..]);
            return true;
        }

        return false;
    }

    private static List<(string Name, string Value)> ParseHeaderLines(ReadOnlySpan<byte> headerSpan, out string statusLine)
    {
        statusLine = string.Empty;
        var result = new List<(string Name, string Value)>(16);

        headerSpan = SkipLeadingBlankLines(headerSpan);
        if (headerSpan.IsEmpty)
            throw new InvalidOperationException("Invalid response header: missing status line.");

        var offset = 0;
        var statusParsed = false;
        while (offset < headerSpan.Length)
        {
            var remaining = headerSpan[offset..];
            var lineEndRelative = remaining.IndexOf((byte)'\n');
            if (lineEndRelative < 0) break;

            var line = remaining[..lineEndRelative];
            if (!line.IsEmpty && line[^1] == (byte)'\r') line = line[..^1];
            offset += lineEndRelative + 1;

            if (!statusParsed)
            {
                statusLine = Encoding.ASCII.GetString(line);
                statusParsed = true;
                continue;
            }

            if (line.IsEmpty) break;

            if (IsHeaderContinuationLine(line))
            {
                if (result.Count > 0)
                {
                    var continuation = Encoding.ASCII.GetString(TextUtils.Trim(line));
                    if (!string.IsNullOrEmpty(continuation))
                    {
                        var last = result[^1];
                        result[^1] = (last.Name, $"{last.Value} {continuation}");
                    }
                }

                continue;
            }

            var colonIndex = line.IndexOf((byte)':');
            if (colonIndex <= 0) continue;

            var name = Encoding.ASCII.GetString(TextUtils.Trim(line[..colonIndex]));
            var value = Encoding.ASCII.GetString(TextUtils.Trim(line[(colonIndex + 1)..]));
            result.Add((name, value));
        }

        if (string.IsNullOrEmpty(statusLine))
            throw new InvalidOperationException("Invalid response header: missing status line.");

        return result;
    }

    private static ReadOnlySpan<byte> SkipLeadingBlankLines(ReadOnlySpan<byte> span)
    {
        var offset = 0;

        while (offset < span.Length)
        {
            if (span[offset] == (byte)'\n')
            {
                offset++;
                continue;
            }

            if (span[offset] == (byte)'\r' &&
                offset + 1 < span.Length &&
                span[offset + 1] == (byte)'\n')
            {
                offset += 2;
                continue;
            }

            break;
        }

        return span[offset..];
    }

    private static bool IsHeaderContinuationLine(ReadOnlySpan<byte> line)
    {
        return !line.IsEmpty && (line[0] == (byte)' ' || line[0] == (byte)'\t');
    }

    private static void AddConnectionTokenHeaders(ReadOnlySpan<char> value, HashSet<string> result)
    {
        var index = 0;

        while (index < value.Length)
        {
            while (index < value.Length && IsConnectionTokenDelimiter(value[index])) index++;
            var start = index;

            while (index < value.Length && !IsConnectionTokenDelimiter(value[index])) index++;

            if (index <= start) continue;

            var token = value[start..index].ToString();
            if (token.Length == 0) continue;
            result.Add(token);
        }
    }

    private static bool IsInterimResponseStatusCode(int statusCode)
    {
        return statusCode >= 100 && statusCode < 200 && statusCode != 101;
    }

    private static ResponseBodyMode DetermineResponseBodyMode(
        HttpMethod requestMethod,
        int statusCode,
        bool isChunked,
        long? contentLength)
    {
        if (requestMethod == HttpMethod.Head) return ResponseBodyMode.None;
        if (statusCode is 204 or 205 or 304) return ResponseBodyMode.None;
        if (statusCode >= 100 && statusCode < 200)
        {
            if (statusCode == 101) return ResponseBodyMode.UpgradedTunnel;
            return ResponseBodyMode.None;
        }

        if (isChunked) return ResponseBodyMode.Chunked;
        if (contentLength.HasValue) return ResponseBodyMode.ContentLength;
        return ResponseBodyMode.UntilClose;
    }

    private static string ReadHeaderValue(ReadOnlySequence<byte> value)
    {
        var span = value.IsSingleSegment ? value.FirstSpan : value.ToArray();
        return Encoding.ASCII.GetString(span);
    }

    private static HashSet<string> ExtractConnectionTokenHeaders(HttpRequest request)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (request.HeaderLines.Count > 0)
        {
            foreach (var (name, value) in request.HeaderLines)
                if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase))
                    AddConnectionTokenHeaders(value, result);
        }
        else
        {
            AddConnectionTokenHeaders(request.Headers, "Connection", result);
            AddConnectionTokenHeaders(request.Headers, "Proxy-Connection", result);
        }

        return result;
    }

    private static void AddConnectionTokenHeaders(ReadOnlySequence<byte> value, HashSet<string> result)
    {
        if (value.Length == 0) return;
        var span = value.IsSingleSegment ? value.FirstSpan : value.ToArray();
        var index = 0;

        while (index < span.Length)
        {
            while (index < span.Length && IsConnectionTokenDelimiter(span[index])) index++;
            var start = index;

            while (index < span.Length && !IsConnectionTokenDelimiter(span[index])) index++;

            if (index <= start) continue;

            var token = Encoding.ASCII.GetString(span.Slice(start, index - start));
            if (token.Length == 0) continue;

            result.Add(token);
        }
    }

    private static void AddConnectionTokenHeaders(
        IDictionary<string, ReadOnlySequence<byte>> headers,
        string headerName,
        HashSet<string> result)
    {
        if (!headers.TryGetValue(headerName, out var value) || value.Length == 0) return;

        var span = value.IsSingleSegment ? value.FirstSpan : value.ToArray();
        var index = 0;

        while (index < span.Length)
        {
            while (index < span.Length && IsConnectionTokenDelimiter(span[index])) index++;
            var start = index;

            while (index < span.Length && !IsConnectionTokenDelimiter(span[index])) index++;

            if (index <= start) continue;

            var token = Encoding.ASCII.GetString(span.Slice(start, index - start));
            if (token.Length == 0) continue;

            result.Add(token);
        }
    }

    private static bool IsConnectionTokenDelimiter(byte value)
    {
        return value switch
        {
            (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'@' or
                (byte)',' or (byte)';' or (byte)':' or (byte)'\\' or (byte)'"' or
                (byte)'/' or (byte)'[' or (byte)']' or (byte)'?' or (byte)'=' or
                (byte)'{' or (byte)'}' or (byte)' ' or (byte)'\t' or (byte)'\r' or
                (byte)'\n' => true,
            _ => false
        };
    }

    private static bool IsConnectionTokenDelimiter(char value)
    {
        return value switch
        {
            '(' or ')' or '<' or '>' or '@' or
                ',' or ';' or ':' or '\\' or '"' or
                '/' or '[' or ']' or '?' or '=' or
                '{' or '}' or ' ' or '\t' or '\r' or '\n' => true,
            _ => false
        };
    }

    private static string FormatHostHeader(string host, int port)
    {
        var includePort = port != 80 && port != 443;
        var displayHost = host;

        if (IPAddress.TryParse(host, out var ipAddress) &&
            ipAddress.AddressFamily == AddressFamily.InterNetworkV6 &&
            !host.StartsWith("[", StringComparison.Ordinal))
            displayHost = $"[{host}]";

        return includePort ? $"{displayHost}:{port}" : displayHost;
    }

    private static ValueTask SendErrorAsync(
        Socket socket,
        int code,
        string status,
        string message,
        CancellationToken token)
    {
        return code switch
        {
            400 => new ValueTask(HtmlErrorPages.BadRequestAsync(socket, message, token)),
            401 => new ValueTask(HtmlErrorPages.UnauthorizedAsync(socket, "TinyProxy", token)),
            403 => new ValueTask(HtmlErrorPages.ForbiddenAsync(socket, message, token)),
            404 => new ValueTask(HtmlErrorPages.NotFoundAsync(socket, message, token)),
            407 => new ValueTask(HtmlErrorPages.ProxyAuthenticationRequiredAsync(socket, "TinyProxy", token)),
            502 => new ValueTask(HtmlErrorPages.BadGatewayAsync(socket, message, token)),
            503 => new ValueTask(HtmlErrorPages.ServiceUnavailableAsync(socket, message, token)),
            504 => new ValueTask(HtmlErrorPages.GatewayTimeoutAsync(socket, message, token)),
            _ => new ValueTask(HtmlErrorPages.SendErrorAsync(socket, code, status, message, token))
        };
    }

    private void LogAccess(Connection connection, HttpRequest request, int statusCode, long bytesSent)
    {
        var method = request.GetMethodToken();
        _accessLogger.LogAccess(_clientIp, method, request.Uri, request.Version, statusCode, bytesSent);
    }

    /// <summary>
    /// Builds request target for forwarding.
    /// Direct connections use origin-form; HTTP upstream uses absolute-form.
    /// </summary>
    private static string GetForwardRequestTarget(string uri, string host, int port, bool useAbsoluteUri)
    {
        return useAbsoluteUri
            ? GetAbsoluteUri(uri, host, port)
            : GetOriginFormTarget(uri);
    }

    private static string GetAbsoluteUri(string uri, string host, int port)
    {
        if (TryGetAbsoluteUriScheme(uri, out var scheme))
        {
            if (scheme.Equals("http".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                scheme.Equals("https".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                // Strip userinfo to avoid forwarding credentials in the request target.
                // SECURITY: avoid forwarding URI userinfo as credentials.
                if (Uri.TryCreate(uri, UriKind.Absolute, out var absoluteHttpUri) &&
                    !string.IsNullOrEmpty(absoluteHttpUri.UserInfo))
                {
                    var pathAndQuery = absoluteHttpUri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped);
                    if (string.IsNullOrEmpty(pathAndQuery)) pathAndQuery = "/";

                    var isHttps = absoluteHttpUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
                    var defaultPort = isHttps ? 443 : 80;
                    var sanitizedPortSuffix = port == defaultPort ? "" : $":{port}";
                    var normalizedScheme = isHttps ? "https" : "http";
                    return $"{normalizedScheme}://{host}{sanitizedPortSuffix}{pathAndQuery}";
                }

                // Keep existing HTTP/HTTPS absolute-form as-is.
                return uri;
            }

            // Rewrite non-HTTP(S) absolute URIs into canonical http://host:port/path form.
            if (Uri.TryCreate(uri, UriKind.Absolute, out var absoluteUri))
            {
                var pathAndQuery = absoluteUri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped);
                if (string.IsNullOrEmpty(pathAndQuery)) pathAndQuery = "/";
                return $"http://{host}:{port}{pathAndQuery}";
            }
        }

        // Use canonical HTTP URI form; default port 80 is omitted.
        var portSuffix = port == 80 ? "" : $":{port}";
        return $"http://{host}{portSuffix}{uri}";
    }

    private bool TryResolveTarget(HttpRequest request, out string host, out int port, out bool unsupportedProtocol)
    {
        unsupportedProtocol = false;

        if (TryGetAbsoluteUriScheme(request.Uri, out var scheme))
        {
            if (scheme.Equals("http".AsSpan(), StringComparison.OrdinalIgnoreCase))
                return request.TryGetTarget(out host, out port);

            if (!scheme.Equals("ftp".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                host = string.Empty;
                port = 0;
                unsupportedProtocol = true;
                return false;
            }

            // Accept ftp:// targets only when upstream proxying is configured.
            if (!_config.HasUpstreamProxyConfigured)
            {
                host = string.Empty;
                port = 0;
                unsupportedProtocol = true;
                return false;
            }

            if (!Uri.TryCreate(request.Uri, UriKind.Absolute, out var absoluteUri))
            {
                host = string.Empty;
                port = 0;
                return false;
            }

            host = absoluteUri.IdnHost;
            if (string.IsNullOrEmpty(host))
            {
                port = 0;
                return false;
            }

            port = absoluteUri.IsDefaultPort ? 80 : absoluteUri.Port;
            return port is >= 1 and <= 65535;
        }

        return request.TryGetTarget(out host, out port);
    }

    private static bool TryGetAbsoluteUriScheme(string uri, out ReadOnlySpan<char> scheme)
    {
        scheme = ReadOnlySpan<char>.Empty;
        if (string.IsNullOrEmpty(uri)) return false;

        var schemeSeparatorIndex = uri.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparatorIndex <= 0) return false;

        scheme = uri.AsSpan(0, schemeSeparatorIndex);
        return true;
    }

    private static string GetOriginFormTarget(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return "/";
        if (uri == "*") return uri;
        if (uri[0] == '/') return uri;

        if (Uri.TryCreate(uri, UriKind.Absolute, out var absoluteUri))
        {
            var pathAndQuery = absoluteUri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped);
            return string.IsNullOrEmpty(pathAndQuery) ? "/" : pathAndQuery;
        }

        var slashIndex = uri.IndexOf('/');
        return slashIndex >= 0 ? uri.Substring(slashIndex) : "/";
    }

    private enum ResponseBodyMode
    {
        None,
        ContentLength,
        Chunked,
        UntilClose,
        UpgradedTunnel
    }

    private sealed class PrebufferedSocketReader
    {
        private readonly Socket _socket;
        private readonly ReadOnlySequence<byte> _prefetchedData;
        private SequencePosition _position;
        private long _socketBytesRead;

        public PrebufferedSocketReader(Socket socket, ReadOnlySequence<byte> prefetchedData)
        {
            _socket = socket;
            _prefetchedData = prefetchedData;
            _position = prefetchedData.Start;
        }

        public long SocketBytesRead => _socketBytesRead;

        public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token)
        {
            var remainingPrefetched = _prefetchedData.Slice(_position);
            if (!remainingPrefetched.IsEmpty)
            {
                var toCopy = (int)Math.Min(remainingPrefetched.Length, destination.Length);
                remainingPrefetched.Slice(0, toCopy).CopyTo(destination.Span);
                _position = remainingPrefetched.GetPosition(toCopy);
                return toCopy;
            }

            var read = await _socket.ReceiveAsync(destination, SocketFlags.None, token).ConfigureAwait(false);
            if (read > 0) _socketBytesRead += read;
            return read;
        }
    }

    private sealed class ResponseForwardingTimeoutException : TimeoutException
    {
        public ResponseForwardingTimeoutException(bool responseStarted)
            : base("Server response timeout")
        {
            ResponseStarted = responseStarted;
        }

        public bool ResponseStarted { get; }
    }

    private sealed class RequestBodyTooLargeException : Exception;
}