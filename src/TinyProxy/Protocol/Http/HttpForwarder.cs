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

        // Check request body size limit
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

        Socket serverSocket = null!;
        long bytesReceived = 0;

        try
        {
            // Check if upstream proxy is configured
            if (_config.UpstreamProxy != null)
            {
                serverSocket = await ConnectViaUpstreamAsync(host, port, token).ConfigureAwait(false);
            }
            else
            {
                serverSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);

                // Apply BindSame if enabled - aligns with tinyproxy C's bindsame
                serverSocket.BindToSameIp(connection.ClientSocket, _config);

                // Apply BindAddresses if configured
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

            // Build modified request
            var useAbsoluteUri = _config.UpstreamProxy?.Type == UpstreamProxyType.Http;
            var requestBuffer = BuildForwardRequest(request, host, port, useAbsoluteUri);
            await serverSocket.SendAllAsync(requestBuffer, token).ConfigureAwait(false);

            // Forward request body (pre-read bytes + remaining bytes from client socket).
            await ForwardRequestBodyAsync(connection.ClientSocket, serverSocket, request, token).ConfigureAwait(false);

            // Read response from server and forward to client
            (bytesSent, bytesReceived) = await ForwardResponseAsync(
                serverSocket,
                connection.ClientSocket,
                request.Method,
                request.Version,
                token).ConfigureAwait(false);

            _stats.AddBytesSent(bytesSent);
            _stats.AddBytesReceived(bytesReceived);

            // Close server socket after response is complete
            // This ensures we don't wait for keep-alive connections
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
            statusCode = 502;
            await SendErrorAsync(connection.ClientSocket, 502, "Bad Gateway", $"Could not connect to {host}:{port}", token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _stats.IncrementFailedRequests();
            statusCode = 504;
            await SendErrorAsync(connection.ClientSocket, 504, "Gateway Timeout", "Server response timeout", token).ConfigureAwait(false);
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
            statusCode = 502;
            await SendErrorAsync(connection.ClientSocket, 502, "Bad Gateway", ex.Message, token).ConfigureAwait(false);
        }
        finally
        {
            // Always dispose server socket
            try
            {
                serverSocket?.Dispose();
            }
            catch (SocketException)
            {
                // Socket already closed or invalid
            }

            LogAccess(connection, request, statusCode, bytesSent);
        }
    }

    private async Task<Socket> ConnectViaUpstreamAsync(string targetHost, int targetPort, CancellationToken token)
    {
        var upstream = _config.UpstreamProxy!;

        // Handle SOCKS upstream proxies
        if (upstream.Type == UpstreamProxyType.Socks4 || upstream.Type == UpstreamProxyType.Socks5)
        {
            var socksProxy = new SocksUpstreamProxy(_logger, upstream, _config.Timeout);
            return await socksProxy.ConnectAsync(targetHost, targetPort, token).ConfigureAwait(false);
        }

        // HTTP upstream proxy
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(upstream.Host, upstream.Port, _config.Timeout, token).ConfigureAwait(false);
        RecordPotentialLoopEndpoint(socket, upstream.Port);

        // Note: For HTTP proxying, the request will be formatted with absolute URI
        // The upstream proxy will handle the actual connection to target
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
        // Use StringBuilder for better performance with string concatenation
        var sb = StringBuilderCache.Acquire();

        try
        {
            // Request line - direct/origin uses origin-form; HTTP upstream uses absolute-form.
            var method = request.GetMethodToken();
            var requestTarget = GetForwardRequestTarget(request.Uri, host, port, useAbsoluteUri);
            var outboundVersion = NormalizeOutboundHttpVersion(request.Version);

            sb.Append(method).Append(' ').Append(requestTarget).Append(' ').Append(outboundVersion);
            sb.Append(ProxyConstants.Crlf);
            sb.Append("Host: ").Append(FormatHostHeader(host, port)).Append(ProxyConstants.Crlf);

            // Headers - filter and modify
            var hopByHopHeaders = ProxyConstants.HopByHopHeadersSet;
            var connectionTokenHeaders = ExtractConnectionTokenHeaders(request);
            string? upstreamViaHeader = null;

            // Apply anonymous filter if enabled (aligns with tinyproxy C's anonymous.c)
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

                // Host is rebuilt from parsed target to match tinyproxy behavior.
                if (string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase))
                    return;

                // We append our own Via later to preserve proxy chain semantics.
                if (_config.AddViaHeader && string.Equals(name, "Via", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(upstreamViaHeader))
                        upstreamViaHeader = ReadHeaderValue(value);
                    return;
                }

                // Apply anonymous filtering (aligns with tinyproxy C's anonymous_search)
                if (_config.IsAnonymousEnabled && !anonymousFilter.IsHeaderAllowed(name)) return;

                // Write header - use span-based parsing to avoid allocation when possible
                sb.Append(name).Append(": ");
                sb.Append(ReadHeaderValue(value));
                sb.Append(ProxyConstants.Crlf);
            }

            if (request.HeaderLines.Count > 0)
            {
                foreach (var header in request.HeaderLines)
                    AppendFilteredHeader(header.Key, header.Value);
            }
            else
            {
                foreach (var header in request.Headers)
                    AppendFilteredHeader(header.Key, header.Value);
            }

            // Add proxy authentication for upstream proxy
            if (_config.UpstreamProxy?.Username != null)
            {
                var credentials = $"{_config.UpstreamProxy.Username}:{_config.UpstreamProxy.Password}";
                var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes(credentials));
                sb.Append("Proxy-Authorization: Basic ").Append(encoded);
                sb.Append(ProxyConstants.Crlf);
            }

            // Add Via header if configured
            // Aligns with tinyproxy C by appending current proxy to existing Via chain.
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

            // Add X-Forwarded-For with actual client IP
            sb.Append("X-Forwarded-For: ").Append(_clientIp).Append(ProxyConstants.Crlf);
            sb.Append("X-Forwarded-Host: ").Append(host).Append(ProxyConstants.Crlf);
            sb.Append("X-Forwarded-Proto: http").Append(ProxyConstants.Crlf);
            sb.Append("Connection: close").Append(ProxyConstants.Crlf);
            if (useAbsoluteUri) sb.Append("Proxy-Connection: close").Append(ProxyConstants.Crlf);

            // Add X-Tinyproxy header if configured
            // Aligns with tinyproxy C's AddXTinyproxy option
            if (_config.AddXTinyproxyHeader) sb.Append("X-Tinyproxy: ").Append(_clientIp).Append(ProxyConstants.Crlf);

            // Add custom headers from configuration
            // Aligns with tinyproxy C's add_headers functionality
            foreach (var header in _config.CustomHeaders)
            {
                sb.Append(header.Name).Append(": ").Append(header.Value);
                sb.Append(ProxyConstants.Crlf);
            }

            sb.Append(ProxyConstants.Crlf); // End of headers

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
        CancellationToken token)
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
            sent += toSend;
        }

        return sent;
    }

    private static string NormalizeOutboundHttpVersion(string? requestVersion)
    {
        if (string.IsNullOrWhiteSpace(requestVersion)) return "HTTP/1.0";
        if (requestVersion.StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase)) return requestVersion;
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

        return int.TryParse(versionPart[..dotIndex], out major) &&
               int.TryParse(versionPart[(dotIndex + 1)..], out minor);
    }

    private async ValueTask ForwardRequestBodyAsync(
        Socket clientSocket,
        Socket serverSocket,
        HttpRequest request,
        CancellationToken token)
    {
        if (HasChunkedTransferEncoding(request))
        {
            await ForwardChunkedBodyAsync(clientSocket, serverSocket, request.Body, token).ConfigureAwait(false);
            return;
        }

        if (!request.ContentLength.HasValue || request.ContentLength.Value <= 0) return;

        var contentLength = request.ContentLength.Value;
        var sentFromBuffer = await SendBodyAsync(serverSocket, request.Body, contentLength, token).ConfigureAwait(false);
        var remainingBytes = contentLength - sentFromBuffer;

        if (remainingBytes <= 0) return;

        await RelayRequestBodyAsync(
            clientSocket,
            serverSocket,
            remainingBytes,
            token).ConfigureAwait(false);
    }

    private async ValueTask ForwardChunkedBodyAsync(
        Socket clientSocket,
        Socket serverSocket,
        ReadOnlySequence<byte> prefetchedBody,
        CancellationToken token)
    {
        var reader = new PrebufferedSocketReader(clientSocket, prefetchedBody);
        var lineBuffer = ArrayPool<byte>.Shared.Rent(ProxyConstants.InitialHeaderBufferSize);
        var copyBuffer = ArrayPool<byte>.Shared.Rent(ProxyConstants.StreamBufferSize);
        long totalPayloadBytes = 0;

        try
        {
            while (true)
            {
                var lineLength = await ReadLineAsync(reader, lineBuffer, token).ConfigureAwait(false);
                await serverSocket.SendAllAsync(lineBuffer.AsMemory(0, lineLength), token).ConfigureAwait(false);

                var chunkSize = ParseChunkSize(lineBuffer.AsMemory(0, lineLength));
                if (chunkSize == 0)
                {
                    await ForwardChunkTrailersAsync(reader, serverSocket, lineBuffer, token).ConfigureAwait(false);
                    break;
                }

                totalPayloadBytes += chunkSize;
                if (_config.MaxRequestSize > 0 && totalPayloadBytes > _config.MaxRequestSize)
                    throw new RequestBodyTooLargeException();

                // Forward chunk payload.
                await CopyExactlyAsync(reader, serverSocket, copyBuffer, chunkSize, token).ConfigureAwait(false);

                // Forward and validate chunk terminator (CRLF or LF).
                var chunkTerminatorLength = await ReadLineAsync(reader, lineBuffer, token).ConfigureAwait(false);
                if (!IsEmptyLine(lineBuffer, chunkTerminatorLength))
                    throw new InvalidOperationException("Invalid chunk terminator.");
                await serverSocket.SendAllAsync(lineBuffer.AsMemory(0, chunkTerminatorLength), token).ConfigureAwait(false);
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
        CancellationToken token)
    {
        var length = 0;
        while (length < lineBuffer.Length)
        {
            var read = await reader.ReadAsync(lineBuffer.AsMemory(length, 1), token).ConfigureAwait(false);
            if (read == 0) throw new InvalidOperationException("Connection closed while reading chunked body.");
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
        CancellationToken token)
    {
        while (true)
        {
            var lineLength = await ReadLineAsync(reader, lineBuffer, token).ConfigureAwait(false);
            await serverSocket.SendAllAsync(lineBuffer.AsMemory(0, lineLength), token).ConfigureAwait(false);

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
        CancellationToken token)
    {
        while (bytesToCopy > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, bytesToCopy);
            var read = await reader.ReadAsync(buffer.AsMemory(0, toRead), token).ConfigureAwait(false);
            if (read == 0) throw new InvalidOperationException("Connection closed while forwarding chunked body.");

            await destination.SendAllAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            bytesToCopy -= read;
        }
    }

    private static async ValueTask RelayRequestBodyAsync(
        Socket clientSocket,
        Socket serverSocket,
        long remainingBytes,
        CancellationToken token)
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

                await serverSocket.SendAllAsync(
                    buffer.AsMemory(0, received),
                    token).ConfigureAwait(false);

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
    /// Parses response framing so forwarding can complete even if upstream keeps the connection alive.
    /// </summary>
    private async Task<(long sent, long received)> ForwardResponseAsync(
        Socket server,
        Socket client,
        HttpMethod requestMethod,
        string requestVersion,
        CancellationToken token)
    {
        var headerBuffer = ArrayPool<byte>.Shared.Rent(ProxyConstants.InitialHeaderBufferSize);
        long totalSent = 0;
        long totalReceived = 0;
        ReadOnlySequence<byte> pendingPrefetched = ReadOnlySequence<byte>.Empty;
        var interimResponsesForwarded = 0;
        var viaProtocolToken = GetViaProtocolToken(requestVersion);
        var omitResponseHeaders = IsHttp09Request(requestVersion);

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
                        token).ConfigureAwait(false);
                    if (received == 0) throw new InvalidOperationException("Connection closed while reading response headers.");

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
                    viaProtocolToken);
                if (!omitResponseHeaders)
                {
                    await client.SendAllAsync(sanitizedHeader, token).ConfigureAwait(false);
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
                            await client.SendAllAsync(prefetchedBody, token).ConfigureAwait(false);
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
                                token).ConfigureAwait(false);
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
                                token).ConfigureAwait(false);
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
                                token).ConfigureAwait(false);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
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
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer);
        }
    }

    private static async ValueTask<long> ForwardFixedLengthBodyAsync(
        PrebufferedSocketReader reader,
        Socket destination,
        byte[] buffer,
        long contentLength,
        CancellationToken token)
    {
        if (contentLength <= 0) return 0;

        long totalSent = 0;
        var remaining = contentLength;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await reader.ReadAsync(buffer.AsMemory(0, toRead), token).ConfigureAwait(false);
            if (read == 0) throw new InvalidOperationException("Connection closed before full response body was received.");

            await destination.SendAllAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
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
        CancellationToken token)
    {
        long totalSent = 0;

        while (true)
        {
            var chunkSizeLineLength = await ReadLineAsync(reader, lineBuffer, token).ConfigureAwait(false);
            await destination.SendAllAsync(lineBuffer.AsMemory(0, chunkSizeLineLength), token).ConfigureAwait(false);
            totalSent += chunkSizeLineLength;

            var chunkSize = ParseChunkSize(lineBuffer.AsMemory(0, chunkSizeLineLength));
            if (chunkSize == 0)
            {
                while (true)
                {
                    var trailerLength = await ReadLineAsync(reader, lineBuffer, token).ConfigureAwait(false);
                    await destination.SendAllAsync(lineBuffer.AsMemory(0, trailerLength), token).ConfigureAwait(false);
                    totalSent += trailerLength;
                    if (IsEmptyLine(lineBuffer, trailerLength)) return totalSent;
                }
            }

            await CopyExactlyAsync(reader, destination, copyBuffer, chunkSize, token).ConfigureAwait(false);
            totalSent += chunkSize;

            var chunkTerminatorLength = await ReadLineAsync(reader, lineBuffer, token).ConfigureAwait(false);
            if (!IsEmptyLine(lineBuffer, chunkTerminatorLength))
                throw new InvalidOperationException("Invalid chunk terminator.");
            await destination.SendAllAsync(lineBuffer.AsMemory(0, chunkTerminatorLength), token).ConfigureAwait(false);
            totalSent += chunkTerminatorLength;
        }
    }

    private static async ValueTask<long> ForwardUntilCloseAsync(
        PrebufferedSocketReader reader,
        Socket destination,
        byte[] buffer,
        CancellationToken token)
    {
        long totalSent = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0) break;

            await destination.SendAllAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            totalSent += read;

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

        bool isChunked = false;
        long? contentLength = null;

        var offset = firstLineEnd + 1;
        while (offset < span.Length)
        {
            var lineEndRelative = span[offset..].IndexOf((byte)'\n');
            if (lineEndRelative < 0) break;

            var lineEnd = offset + lineEndRelative;
            var line = span[offset..lineEnd];
            if (!line.IsEmpty && line[^1] == (byte)'\r') line = line[..^1];

            if (line.IsEmpty) break;

            var colonIndex = line.IndexOf((byte)':');
            if (colonIndex > 0)
            {
                var name = TextUtils.Trim(line[..colonIndex]);
                var value = TextUtils.Trim(line[(colonIndex + 1)..]);

                if (HeaderNameEquals(name, "Transfer-Encoding"u8))
                {
                    isChunked = TextUtils.IndexOfIgnoreCase(value, "chunked"u8) >= 0;
                }
                else if (HeaderNameEquals(name, "Content-Length"u8) &&
                         Utf8Parser.TryParse(value, out long parsedLength, out var consumed) &&
                         consumed == value.Length &&
                         parsedLength >= 0)
                {
                    contentLength = parsedLength;
                }
            }

            offset = lineEnd + 1;
        }

        return (statusCode, isChunked, contentLength);
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
        {
            if (!EqualsIgnoreCaseAscii(name[i], expected[i])) return false;
        }

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
        string viaProtocolToken)
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
            if (statusCode == 101) return ResponseBodyMode.UntilClose;
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
            {
                if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase))
                    AddConnectionTokenHeaders(value, result);
            }
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
            // Keep existing HTTP/HTTPS absolute-form as-is.
            if (scheme.Equals("http".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                scheme.Equals("https".AsSpan(), StringComparison.OrdinalIgnoreCase))
                return uri;

            // tinyproxy C rewrites upstream absolute-form to http://host:port/path.
            if (Uri.TryCreate(uri, UriKind.Absolute, out var absoluteUri))
            {
                var pathAndQuery = absoluteUri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped);
                if (string.IsNullOrEmpty(pathAndQuery)) pathAndQuery = "/";
                return $"http://{host}:{port}{pathAndQuery}";
            }
        }

        // Build absolute URI
        // Omit port for standard ports (80 for http, 443 for https) - matches tinyproxy C behavior
        var portSuffix = port == 80 ? "" : $":{port}";
        return $"http://{host}{portSuffix}{uri}";
    }

    private bool TryResolveTarget(HttpRequest request, out string host, out int port, out bool unsupportedProtocol)
    {
        unsupportedProtocol = false;

        if (TryGetAbsoluteUriScheme(request.Uri, out var scheme))
        {
            if (scheme.Equals("http".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                scheme.Equals("https".AsSpan(), StringComparison.OrdinalIgnoreCase))
                return request.TryGetTarget(out host, out port);

            if (!scheme.Equals("ftp".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                host = string.Empty;
                port = 0;
                unsupportedProtocol = true;
                return false;
            }

            // tinyproxy C only accepts ftp:// when an upstream proxy is configured.
            if (_config.UpstreamProxy == null)
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
        UntilClose
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

    private sealed class RequestBodyTooLargeException : Exception;
}
