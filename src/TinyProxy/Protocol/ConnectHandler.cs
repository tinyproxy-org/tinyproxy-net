using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TinyProxy.Config;
using TinyProxy.Core;
using TinyProxy.Filter;
using TinyProxy.Logging;
using TinyProxy.Metrics;

namespace TinyProxy.Protocol;

/// <summary>
/// Handles HTTPS CONNECT tunnel requests.
/// </summary>
public sealed class ConnectHandler
{
    private readonly ILogger _logger;
    private readonly Configuration _config;
    private readonly Stats _stats;
    private readonly AccessLogger _accessLogger;
    private readonly string _clientIp;
    private readonly LoopDetector? _loopDetector;

    private static readonly byte[] s_establishedResponseHttp10 = Encoding.ASCII.GetBytes(
        "HTTP/1.0 200 Connection established\r\nProxy-agent: TinyProxy.NET\r\n\r\n");

    private static readonly byte[] s_establishedResponseHttp11 = Encoding.ASCII.GetBytes(
        "HTTP/1.1 200 Connection established\r\nProxy-agent: TinyProxy.NET\r\n\r\n");

    public ConnectHandler(
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

    public async ValueTask HandleConnectAsync(
        Connection connection,
        Http.HttpRequest request,
        ReadOnlySequence<byte> initialData,
        CancellationToken token)
    {
        // Parse host:port from CONNECT request
        if (!TryParseConnectTarget(request.Uri, out var host, out var port))
        {
            _stats.IncrementFailedRequests();
            await HtmlErrorPages.BadRequestAsync(
                connection.ClientSocket,
                "Invalid CONNECT target",
                token);
            LogConnect(request, host, port, false);
            return;
        }

        if (_config.Verbose) _logger.LogInfo($"CONNECT {host}:{port}");

        // Check if port is allowed
        var filter = new Filter.ConnectFilter(_config);
        if (!filter.IsPortAllowed((ushort)port))
        {
            _logger.LogWarning($"CONNECT port {port} not allowed");
            _stats.IncrementFailedRequests();
            await HtmlErrorPages.ForbiddenAsync(
                connection.ClientSocket,
                $"Port {port} is not allowed for CONNECT",
                token);
            LogConnect(request, host, port, false);
            return;
        }

        try
        {
            // Connect to target server (directly or through configured upstream proxy).
            var (serverSocket, serverInitialData, upstreamResponseHeader, upstreamStatusCode) = await ConnectToTunnelEndpointAsync(
                host,
                port,
                request,
                token).ConfigureAwait(false);
            using (serverSocket)
            {
                long prefixedServerToClientBytes = 0;
                if (upstreamStatusCode > 0)
                {
                    var upstreamContentLength = TryParseContentLength(upstreamResponseHeader.Span);
                    var sanitizedHeader = Http.HttpForwarder.BuildForwardResponseHeader(
                        upstreamResponseHeader.Span,
                        upstreamStatusCode,
                        _config.AddViaHeader,
                        _config.ViaProxyName,
                        GetViaProtocolToken(request.Version));

                    // Align with tinyproxy C behavior for CONNECT over HTTP upstream:
                    // forward upstream response to client instead of generating local 200.
                    await connection.ClientSocket.SendAllAsync(sanitizedHeader, token).ConfigureAwait(false);
                    prefixedServerToClientBytes += sanitizedHeader.Length;

                    if (upstreamStatusCode != 200)
                    {
                        var remainingServerBytes = await ForwardUpstreamResponseBodyAsync(
                            serverSocket,
                            connection.ClientSocket,
                            serverInitialData,
                            upstreamContentLength,
                            token).ConfigureAwait(false);

                        _stats.IncrementFailedRequests();
                        _stats.AddBytesSent(prefixedServerToClientBytes + remainingServerBytes);
                        LogConnect(request, host, port, false);
                        return;
                    }
                }
                else
                {
                    // Direct/SOCKS CONNECT response generated locally (tinyproxy C direct behavior).
                    await connection.ClientSocket.SendAllAsync(
                        GetEstablishedResponse(request.Version),
                        token).ConfigureAwait(false);
                }

                // Start bidirectional tunnel with timeout
                var (bytesToServer, bytesToClient) = await RunTunnelAsync(
                    connection.ClientSocket,
                    serverSocket,
                    initialData,
                    serverInitialData,
                    token).ConfigureAwait(false);

                _stats.AddBytesSent(prefixedServerToClientBytes + bytesToClient);
                _stats.AddBytesReceived(bytesToServer);
                LogConnect(request, host, port, true);
            }
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            _stats.IncrementFailedRequests();
            await HtmlErrorPages.BadGatewayAsync(
                connection.ClientSocket,
                $"Connection to {host}:{port} refused",
                token);
            LogConnect(request, host, port, false);
        }
        catch (TimeoutException)
        {
            _stats.IncrementFailedRequests();
            await HtmlErrorPages.GatewayTimeoutAsync(
                connection.ClientSocket,
                $"Connection to {host}:{port} timed out",
                token);
            LogConnect(request, host, port, false);
        }
        catch (Exception ex)
        {
            _logger.LogError($"CONNECT error: {ex.Message}");
            _stats.IncrementFailedRequests();
            await HtmlErrorPages.BadGatewayAsync(
                connection.ClientSocket,
                ex.Message,
                token);
            LogConnect(request, host, port, false);
        }
    }

    private static bool TryParseConnectTarget(string uri, out string host, out int port)
    {
        return TextUtils.TryParseHostPort(uri, 443, out host, out port);
    }

    private static ReadOnlyMemory<byte> GetEstablishedResponse(string? requestVersion)
    {
        if (string.Equals(requestVersion, "HTTP/1.1", StringComparison.OrdinalIgnoreCase))
            return s_establishedResponseHttp11;

        if (TryGetHttp1MinorVersion(requestVersion, out var minorVersion))
        {
            if (minorVersion == 1) return s_establishedResponseHttp11;
            if (minorVersion == 0) return s_establishedResponseHttp10;
            return Encoding.ASCII.GetBytes($"HTTP/1.{minorVersion} 200 Connection established\r\nProxy-agent: TinyProxy.NET\r\n\r\n");
        }

        // tinyproxy C falls back to HTTP/1.0 when the parsed major version is not 1.
        return s_establishedResponseHttp10;
    }

    private static bool TryGetHttp1MinorVersion(string? requestVersion, out int minor)
    {
        minor = 0;
        if (string.IsNullOrWhiteSpace(requestVersion)) return false;
        if (!requestVersion.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)) return false;

        var versionPart = requestVersion.AsSpan(5);
        var dotIndex = versionPart.IndexOf('.');
        if (dotIndex <= 0 || dotIndex >= versionPart.Length - 1) return false;

        if (!int.TryParse(versionPart[..dotIndex], out var major) || major != 1)
            return false;

        return int.TryParse(versionPart[(dotIndex + 1)..], out minor);
    }

    private void RecordPotentialLoopEndpoint(Socket socket, int destinationPort)
    {
        if (_loopDetector == null) return;
        if (destinationPort != _config.ListenPort) return;
        _loopDetector.RecordOutgoingLocalEndpoint(socket.LocalEndPoint);
    }

    private async ValueTask<(Socket socket, ReadOnlySequence<byte> serverInitialData, ReadOnlyMemory<byte> upstreamResponseHeader, int upstreamStatusCode)> ConnectToTunnelEndpointAsync(
        string targetHost,
        int targetPort,
        Http.HttpRequest request,
        CancellationToken token)
    {
        if (_config.UpstreamProxy == null)
        {
            var directSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await directSocket.ConnectAsync(targetHost, targetPort, _config.Timeout, token).ConfigureAwait(false);
            RecordPotentialLoopEndpoint(directSocket, targetPort);
            return (directSocket, ReadOnlySequence<byte>.Empty, ReadOnlyMemory<byte>.Empty, 0);
        }

        var upstream = _config.UpstreamProxy;
        if (upstream.Type is UpstreamProxyType.Socks4 or UpstreamProxyType.Socks5)
        {
            var socksProxy = new SocksUpstreamProxy(_logger, upstream, _config.Timeout);
            var socket = await socksProxy.ConnectAsync(targetHost, targetPort, token).ConfigureAwait(false);
            return (socket, ReadOnlySequence<byte>.Empty, ReadOnlyMemory<byte>.Empty, 0);
        }

        var upstreamSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await upstreamSocket.ConnectAsync(upstream.Host, upstream.Port, _config.Timeout, token).ConfigureAwait(false);
            RecordPotentialLoopEndpoint(upstreamSocket, upstream.Port);

            var connectRequest = BuildHttpUpstreamConnectRequest(request, targetHost, targetPort, upstream);
            await upstreamSocket.SendAllAsync(connectRequest, token).ConfigureAwait(false);

            var (statusCode, responseHeader, serverInitialData) = await ReadHttpUpstreamConnectResponseAsync(upstreamSocket, token).ConfigureAwait(false);
            return (upstreamSocket, serverInitialData, responseHeader, statusCode);
        }
        catch
        {
            upstreamSocket.Dispose();
            throw;
        }
    }

    private byte[] BuildHttpUpstreamConnectRequest(
        Http.HttpRequest request,
        string targetHost,
        int targetPort,
        UpstreamProxyConfig upstream)
    {
        var sb = StringBuilderCache.Acquire(256);
        try
        {
            sb.Append("CONNECT ")
                .Append(targetHost)
                .Append(':')
                .Append(targetPort)
                .Append(' ')
                .Append(NormalizeOutboundHttpVersion(request.Version))
                .Append(ProxyConstants.Crlf);

            sb.Append("Host: ")
                .Append(FormatHostHeader(targetHost, targetPort))
                .Append(ProxyConstants.Crlf);

            sb.Append("Connection: close").Append(ProxyConstants.Crlf);

            if (!string.IsNullOrEmpty(upstream.Username))
            {
                var credentials = $"{upstream.Username}:{upstream.Password}";
                var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes(credentials));
                sb.Append("Proxy-Authorization: Basic ")
                    .Append(encoded)
                    .Append(ProxyConstants.Crlf);
            }

            var connectionTokenHeaders = ExtractConnectionTokenHeaders(request);
            var anonymousFilter = new AnonymousFilter(_config.AnonymousAllowedHeaders);
            string? upstreamViaHeader = null;

            void AppendFilteredHeader(string name, ReadOnlySequence<byte> value)
            {
                if (connectionTokenHeaders.Contains(name)) return;
                if (ShouldSkipConnectClientHeader(name)) return;

                if (_config.AddViaHeader && name.Equals("Via", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(upstreamViaHeader))
                        upstreamViaHeader = ReadHeaderValue(value);
                    return;
                }

                if (_config.IsAnonymousEnabled && !anonymousFilter.IsHeaderAllowed(name)) return;

                sb.Append(name).Append(": ").Append(ReadHeaderValue(value)).Append(ProxyConstants.Crlf);
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

            if (_config.AddXTinyproxyHeader)
                sb.Append("X-Tinyproxy: ").Append(_clientIp).Append(ProxyConstants.Crlf);

            foreach (var customHeader in _config.CustomHeaders)
            {
                sb.Append(customHeader.Name).Append(": ").Append(customHeader.Value).Append(ProxyConstants.Crlf);
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

    private static string NormalizeOutboundHttpVersion(string? requestVersion)
    {
        if (TryGetHttp1MinorVersion(requestVersion, out var minorVersion))
            return $"HTTP/1.{minorVersion}";

        return "HTTP/1.0";
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

    private static bool ShouldSkipConnectClientHeader(string name)
    {
        return name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Te", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Trailers", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetViaProtocolToken(string? requestVersion)
    {
        if (TryGetHttp1MinorVersion(requestVersion, out var minorVersion))
            return $"1.{minorVersion}";

        return "1.0";
    }

    private static string ReadHeaderValue(ReadOnlySequence<byte> value)
    {
        var span = value.IsSingleSegment ? value.FirstSpan : value.ToArray();
        return Encoding.ASCII.GetString(span);
    }

    private static HashSet<string> ExtractConnectionTokenHeaders(Http.HttpRequest request)
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

    private async ValueTask<(int statusCode, ReadOnlyMemory<byte> responseHeader, ReadOnlySequence<byte> serverInitialData)> ReadHttpUpstreamConnectResponseAsync(
        Socket socket,
        CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ProxyConstants.InitialHeaderBufferSize);
        var buffered = 0;

        try
        {
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                if (buffered == buffer.Length)
                {
                    if (buffer.Length >= ProxyConstants.MaxHeaderSize)
                        throw new InvalidOperationException("Upstream CONNECT response headers too large.");

                    var newSize = Math.Min(buffer.Length * 2, ProxyConstants.MaxHeaderSize);
                    var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
                    Buffer.BlockCopy(buffer, 0, newBuffer, 0, buffered);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = newBuffer;
                }

                var read = await socket.ReceiveAsync(buffer.AsMemory(buffered), SocketFlags.None, token).ConfigureAwait(false);
                if (read == 0)
                    throw new InvalidOperationException("Upstream proxy closed connection before CONNECT response was complete.");

                buffered += read;

                if (TryFindHeadersEnd(buffer.AsSpan(0, buffered), out var foundHeaderEnd))
                    headerEnd = foundHeaderEnd;
            }

            var statusCode = ParseResponseStatusCode(buffer.AsSpan(0, headerEnd));
            var responseHeader = buffer.AsMemory(0, headerEnd).ToArray();
            if (buffered == headerEnd)
                return (statusCode, responseHeader, ReadOnlySequence<byte>.Empty);

            var initialData = buffer.AsMemory(headerEnd, buffered - headerEnd).ToArray();
            return (statusCode, responseHeader, new ReadOnlySequence<byte>(initialData));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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

    private static int ParseResponseStatusCode(ReadOnlySpan<byte> responseHeader)
    {
        var span = SkipLeadingBlankLines(responseHeader);
        if (span.IsEmpty)
            throw new InvalidOperationException("Invalid upstream CONNECT response.");

        var lineEnd = span.IndexOf((byte)'\n');
        if (lineEnd < 0)
            throw new InvalidOperationException("Invalid upstream CONNECT response.");

        var statusLine = span[..lineEnd];
        if (!statusLine.IsEmpty && statusLine[^1] == (byte)'\r') statusLine = statusLine[..^1];

        var firstSpace = statusLine.IndexOf((byte)' ');
        if (firstSpace < 0 || firstSpace + 1 >= statusLine.Length)
            throw new InvalidOperationException("Invalid upstream CONNECT status line.");

        var remainder = statusLine[(firstSpace + 1)..];
        var secondSpace = remainder.IndexOf((byte)' ');
        var codeSpan = secondSpace >= 0 ? remainder[..secondSpace] : remainder;

        if (!Utf8Parser.TryParse(codeSpan, out int statusCode, out var consumed) ||
            consumed != codeSpan.Length)
            throw new InvalidOperationException("Invalid upstream CONNECT status code.");

        return statusCode;
    }

    private static long? TryParseContentLength(ReadOnlySpan<byte> responseHeader)
    {
        var span = SkipLeadingBlankLines(responseHeader);
        if (span.IsEmpty) return null;

        var firstLineEnd = span.IndexOf((byte)'\n');
        if (firstLineEnd < 0) return null;

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

                if (HeaderNameEquals(name, "Content-Length"u8) &&
                    Utf8Parser.TryParse(value, out long parsedLength, out var consumed) &&
                    consumed == value.Length &&
                    parsedLength >= 0)
                {
                    return parsedLength;
                }
            }

            offset = lineEnd + 1;
        }

        return null;
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

    private async Task<(long toServer, long toClient)> RunTunnelAsync(
        Socket client,
        Socket server,
        ReadOnlySequence<byte> initialClientData,
        ReadOnlySequence<byte> initialServerData,
        CancellationToken token)
    {
        // Use timeout to prevent hanging connections and resource exhaustion
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(_config.ConnectIdleTimeout);

        // Run both directions concurrently
        var clientToServer = CopyDataAsync(client, server, "Client->Server", initialClientData, cts.Token);
        var serverToClient = CopyDataAsync(server, client, "Server->Client", initialServerData, cts.Token);

        // Tunnel closes when either direction completes.
        await Task.WhenAny(clientToServer, serverToClient).ConfigureAwait(false);
        cts.Cancel();

        var toServer = await clientToServer.ConfigureAwait(false);
        var toClient = await serverToClient.ConfigureAwait(false);

        // Try to shutdown both sockets
        try
        {
            server.Shutdown(SocketShutdown.Both);
            client.Shutdown(SocketShutdown.Both);
        }
        catch
        {
            // Ignore shutdown errors
        }

        return (toServer, toClient);
    }

    private async Task<long> ForwardUpstreamResponseBodyAsync(
        Socket source,
        Socket destination,
        ReadOnlySequence<byte> initialData,
        long? contentLength,
        CancellationToken token)
    {
        if (contentLength.HasValue)
            return await CopyFixedLengthDataAsync(
                source,
                destination,
                initialData,
                contentLength.Value,
                token).ConfigureAwait(false);

        return await CopyDataAsync(
            source,
            destination,
            "Server->Client",
            initialData,
            token).ConfigureAwait(false);
    }

    private static async Task<long> CopyFixedLengthDataAsync(
        Socket source,
        Socket destination,
        ReadOnlySequence<byte> initialData,
        long contentLength,
        CancellationToken token)
    {
        if (contentLength <= 0) return 0;

        long totalBytes = 0;
        var remaining = contentLength;

        foreach (var segment in initialData)
        {
            if (remaining == 0) break;

            var toSend = (int)Math.Min(segment.Length, remaining);
            if (toSend > 0)
            {
                await destination.SendAllAsync(segment.Slice(0, toSend), token).ConfigureAwait(false);
                totalBytes += toSend;
                remaining -= toSend;
            }
        }

        if (remaining == 0) return totalBytes;

        var buffer = ArrayPool<byte>.Shared.Rent(ProxyConstants.DefaultBufferSize);
        try
        {
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = await source.ReceiveAsync(buffer.AsMemory(0, toRead), SocketFlags.None, token).ConfigureAwait(false);
                if (read == 0) break;

                await destination.SendAllAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                totalBytes += read;
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return totalBytes;
    }

    /// <summary>
    /// Copies data between sockets with optimized buffer sizing.
    /// Uses larger buffer (64KB) for better throughput on high-speed networks.
    /// </summary>
    private async Task<long> CopyDataAsync(
        Socket source,
        Socket destination,
        string direction,
        ReadOnlySequence<byte> initialData,
        CancellationToken token)
    {
        // Use larger buffer for tunnel data transfer
        const int TunnelBufferSize = 65536;

        var buffer = ArrayPool<byte>.Shared.Rent(TunnelBufferSize);
        long totalBytes = 0;

        try
        {
            // First, send any initial data we have
            if (initialData.Length > 0)
                foreach (var segment in initialData)
                {
                    totalBytes += segment.Length;
                    await destination.SendAllAsync(segment, token).ConfigureAwait(false);
                }

            // Then copy data continuously
            int received;
            while ((received = await source.ReceiveAsync(buffer, SocketFlags.None, token).ConfigureAwait(false)) > 0)
            {
                totalBytes += received;
                await destination.SendAllAsync(buffer.AsMemory(0, received), token).ConfigureAwait(false);

                // Cooperative yield for fairness under high load
                if (received > 32768) await Task.Yield();
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            // Expected when connection closes or timeout.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return totalBytes;
    }

    private void LogConnect(Http.HttpRequest request, string host, int port, bool success)
    {
        _accessLogger.LogConnect(_clientIp, host, port, success);
    }
}
