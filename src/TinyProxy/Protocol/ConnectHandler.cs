using System.Buffers;
using System.Net.Sockets;
using System.Text;
using TinyProxy.Config;
using TinyProxy.Core;
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
    private const int BufferSize = 8192;
    private static readonly byte[] s_establishedResponse = Encoding.ASCII.GetBytes(
        "HTTP/1.1 200 Connection Established\r\n\r\n");

    public ConnectHandler(
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

    public async ValueTask HandleConnectAsync(
        Core.Connection connection,
        Protocol.Http.HttpRequest request,
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

        if (_config.Verbose)
        {
            _logger.LogInfo($"CONNECT {host}:{port}");
        }

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
            // Connect to target server
            var serverSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await serverSocket.ConnectAsync(host, port, _config.Timeout, token).ConfigureAwait(false);

            using (serverSocket)
            {
                // Send 200 Connection Established to client
                await connection.ClientSocket.SendAsync(
                    s_establishedResponse,
                    SocketFlags.None,
                    token).ConfigureAwait(false);

                // Start bidirectional tunnel with timeout
                var (bytesToServer, bytesToClient) = await RunTunnelAsync(
                    connection.ClientSocket,
                    serverSocket,
                    initialData,
                    token).ConfigureAwait(false);

                _stats.AddBytesSent(bytesToClient);
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
        host = string.Empty;
        port = 443;

        if (string.IsNullOrWhiteSpace(uri))
        {
            return false;
        }

        // Parse host:port
        // Handle IPv6 addresses [::1]:port
        var bracketStart = uri.IndexOf('[');
        if (bracketStart >= 0)
        {
            var bracketEnd = uri.IndexOf(']', bracketStart);
            if (bracketEnd < 0) return false;

            host = uri.Substring(bracketStart + 1, bracketEnd - bracketStart - 1);

            if (bracketEnd + 1 < uri.Length && uri[bracketEnd + 1] == ':')
            {
                _ = int.TryParse(uri.Substring(bracketEnd + 2), out port);
            }
        }
        else
        {
            var colonIndex = uri.LastIndexOf(':');
            if (colonIndex >= 0)
            {
                host = uri.Substring(0, colonIndex);
                _ = int.TryParse(uri.Substring(colonIndex + 1), out port);
            }
            else
            {
                host = uri;
            }
        }

        return !string.IsNullOrEmpty(host) && port > 0 && port < 65536;
    }

    private async Task<(long toServer, long toClient)> RunTunnelAsync(
        Socket client,
        Socket server,
        ReadOnlySequence<byte> initialData,
        CancellationToken token)
    {
        // Use timeout to prevent hanging connections and resource exhaustion
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(_config.ConnectIdleTimeout);

        // Run both directions concurrently
        var clientToServer = CopyDataAsync(client, server, "Client->Server", initialData, cts.Token);
        var serverToClient = CopyDataAsync(server, client, "Server->Client", ReadOnlySequence<byte>.Empty, cts.Token);

        await Task.WhenAny(clientToServer, serverToClient).ConfigureAwait(false);

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
            {
                foreach (var segment in initialData)
                {
                    totalBytes += segment.Length;
                    await destination.SendAsync(segment, SocketFlags.None, token).ConfigureAwait(false);
                }
            }

            // Then copy data continuously
            int received;
            while ((received = await source.ReceiveAsync(buffer, SocketFlags.None, token).ConfigureAwait(false)) > 0)
            {
                totalBytes += received;
                await destination.SendAsync(buffer.AsMemory(0, received), SocketFlags.None, token).ConfigureAwait(false);

                // Cooperative yield for fairness under high load
                if (received > 32768)
                {
                    await Task.Yield();
                }
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            // Expected when connection closes or timeout
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return totalBytes;
    }

    private void LogConnect(Protocol.Http.HttpRequest request, string host, int port, bool success)
    {
        _accessLogger.LogConnect(_clientIp, host, port, success);
    }
}
