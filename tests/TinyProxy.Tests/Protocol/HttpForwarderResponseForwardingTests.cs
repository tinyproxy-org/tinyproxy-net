namespace TinyProxy.Tests.Protocol;

public class HttpForwarderResponseForwardingTests
{
    private static readonly MethodInfo s_forwardResponseAsyncMethod =
        typeof(HttpForwarder).GetMethod(
            "ForwardResponseAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public async Task ForwardResponseAsync_HandlesInterim100ThenFinalContentLength()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var upstream = await CreateConnectedSocketsAsync(cts.Token);
        using var downstream = await CreateConnectedSocketsAsync(cts.Token);

        var forwarder = CreateForwarder();
        var forwardTask = InvokeForwardResponseAsync(
            forwarder,
            upstream.ProxySide,
            downstream.ProxySide,
            TinyProxy.Protocol.Http.HttpMethod.Get,
            "HTTP/1.1",
            null,
            cts.Token);

        var responseBytes =
            "HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: keep-alive\r\n\r\nhello"u8.ToArray();
        var expected =
            "HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello";

        await upstream.PeerSide.SendAllAsync(responseBytes, cts.Token);

        var received = await ReceiveExactlyAsync(downstream.PeerSide, Encoding.ASCII.GetByteCount(expected), cts.Token);
        var (sent, read) = await forwardTask;
        var text = Encoding.ASCII.GetString(received);

        Assert.Equal(expected, text);
        Assert.Equal(received.Length, sent);
        Assert.Equal(responseBytes.Length, read);
    }

    [Fact]
    public async Task ForwardResponseAsync_For101_StreamsUntilClose()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var upstream = await CreateConnectedSocketsAsync(cts.Token);
        using var downstream = await CreateConnectedSocketsAsync(cts.Token);

        var forwarder = CreateForwarder();
        var forwardTask = InvokeForwardResponseAsync(
            forwarder,
            upstream.ProxySide,
            downstream.ProxySide,
            TinyProxy.Protocol.Http.HttpMethod.Get,
            "HTTP/1.1",
            null,
            cts.Token);

        var headers =
            "HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\n"u8.ToArray();
        var payload = "stream-data"u8.ToArray();
        var expected = new byte[headers.Length + payload.Length];
        Buffer.BlockCopy(headers, 0, expected, 0, headers.Length);
        Buffer.BlockCopy(payload, 0, expected, headers.Length, payload.Length);

        await upstream.PeerSide.SendAllAsync(headers, cts.Token);
        await Task.Delay(50, cts.Token);
        await upstream.PeerSide.SendAllAsync(payload, cts.Token);
        upstream.PeerSide.Shutdown(SocketShutdown.Send);

        var received = await ReceiveExactlyAsync(downstream.PeerSide, expected.Length, cts.Token);
        downstream.PeerSide.Shutdown(SocketShutdown.Send);
        var (sent, _) = await forwardTask;

        Assert.Equal(expected, received);
        Assert.Equal(expected.Length, sent);
    }

    [Fact]
    public async Task ForwardResponseAsync_For101_EnablesBidirectionalRelay()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var upstream = await CreateConnectedSocketsAsync(cts.Token);
        using var downstream = await CreateConnectedSocketsAsync(cts.Token);

        var forwarder = CreateForwarder();
        var forwardTask = InvokeForwardResponseAsync(
            forwarder,
            upstream.ProxySide,
            downstream.ProxySide,
            TinyProxy.Protocol.Http.HttpMethod.Get,
            "HTTP/1.1",
            null,
            cts.Token);

        var headers =
            "HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\n"u8.ToArray();
        await upstream.PeerSide.SendAllAsync(headers, cts.Token);

        var forwardedHeaders = await ReceiveExactlyAsync(downstream.PeerSide, headers.Length, cts.Token);
        Assert.Equal(headers, forwardedHeaders);

        var clientPayload = "from-client"u8.ToArray();
        await downstream.PeerSide.SendAllAsync(clientPayload, cts.Token);
        var receivedByServer = await ReceiveExactlyAsync(upstream.PeerSide, clientPayload.Length, cts.Token);
        Assert.Equal(clientPayload, receivedByServer);

        var serverPayload = "from-server"u8.ToArray();
        await upstream.PeerSide.SendAllAsync(serverPayload, cts.Token);
        var receivedByClient = await ReceiveExactlyAsync(downstream.PeerSide, serverPayload.Length, cts.Token);
        Assert.Equal(serverPayload, receivedByClient);

        downstream.PeerSide.Shutdown(SocketShutdown.Send);
        upstream.PeerSide.Shutdown(SocketShutdown.Send);

        await forwardTask;
    }

    [Fact]
    public async Task ForwardResponseAsync_For101_ClientHalfClose_DoesNotTruncateServerToClientFlow()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var upstream = await CreateConnectedSocketsAsync(cts.Token);
        using var downstream = await CreateConnectedSocketsAsync(cts.Token);

        var forwarder = CreateForwarder();
        var forwardTask = InvokeForwardResponseAsync(
            forwarder,
            upstream.ProxySide,
            downstream.ProxySide,
            TinyProxy.Protocol.Http.HttpMethod.Get,
            "HTTP/1.1",
            null,
            cts.Token);

        var headers =
            "HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\n"u8.ToArray();
        await upstream.PeerSide.SendAllAsync(headers, cts.Token);

        var forwardedHeaders = await ReceiveExactlyAsync(downstream.PeerSide, headers.Length, cts.Token);
        Assert.Equal(headers, forwardedHeaders);

        var clientPayload = "ping"u8.ToArray();
        await downstream.PeerSide.SendAllAsync(clientPayload, cts.Token);
        downstream.PeerSide.Shutdown(SocketShutdown.Send);
        var receivedByServer = await ReceiveExactlyAsync(upstream.PeerSide, clientPayload.Length, cts.Token);
        Assert.Equal(clientPayload, receivedByServer);

        await Task.Delay(100, cts.Token);
        var serverPayload = "pong"u8.ToArray();
        await upstream.PeerSide.SendAllAsync(serverPayload, cts.Token);
        upstream.PeerSide.Shutdown(SocketShutdown.Send);

        var receivedByClient = await ReceiveExactlyAsync(downstream.PeerSide, serverPayload.Length, cts.Token);
        Assert.Equal(serverPayload, receivedByClient);

        await forwardTask;
    }

    [Fact]
    public async Task ForwardResponseAsync_SkipsLeadingBlankLinesBeforeStatusLine()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var upstream = await CreateConnectedSocketsAsync(cts.Token);
        using var downstream = await CreateConnectedSocketsAsync(cts.Token);

        var forwarder = CreateForwarder();
        var forwardTask = InvokeForwardResponseAsync(
            forwarder,
            upstream.ProxySide,
            downstream.ProxySide,
            TinyProxy.Protocol.Http.HttpMethod.Get,
            "HTTP/1.1",
            null,
            cts.Token);

        var responseBytes =
            "\r\n\r\nHTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello"u8.ToArray();
        var expected = "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello";

        await upstream.PeerSide.SendAllAsync(responseBytes, cts.Token);

        var received = await ReceiveExactlyAsync(downstream.PeerSide, Encoding.ASCII.GetByteCount(expected), cts.Token);
        var (sent, _) = await forwardTask;
        var text = Encoding.ASCII.GetString(received);

        Assert.Equal(expected, text);
        Assert.Equal(received.Length, sent);
    }

    private static HttpForwarder CreateForwarder()
    {
        var config = Configuration.Default with
        {
            Verbose = false,
            AddViaHeader = false
        };

        return new HttpForwarder(
            new NullLogger(),
            config,
            new Stats(),
            new AccessLogger(config, new NullLogger()),
            "127.0.0.1");
    }

    private static async Task<(long sent, long read)> InvokeForwardResponseAsync(
        HttpForwarder forwarder,
        Socket serverSocket,
        Socket clientSocket,
        TinyProxy.Protocol.Http.HttpMethod method,
        string requestVersion,
        string? reverseMagicCookiePath,
        CancellationToken token)
    {
        var task = (Task<(long sent, long received)>)s_forwardResponseAsyncMethod.Invoke(
            forwarder,
            new object?[] { serverSocket, clientSocket, method, requestVersion, reverseMagicCookiePath, token })!;

        var result = await task;
        return (result.sent, result.received);
    }

    private static async Task<byte[]> ReceiveExactlyAsync(Socket socket, int length, CancellationToken token)
    {
        var buffer = new byte[length];
        var offset = 0;

        while (offset < length)
        {
            var read = await socket.ReceiveAsync(
                buffer.AsMemory(offset),
                SocketFlags.None,
                token);

            if (read == 0) throw new EndOfStreamException("Socket closed before expected data was received.");
            offset += read;
        }

        return buffer;
    }

    private static async Task<SocketPair> CreateConnectedSocketsAsync(CancellationToken token)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var connectSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            var acceptTask = listener.AcceptSocketAsync(token);

            await connectSocket.ConnectAsync(endpoint, token);
            var acceptedSocket = await acceptTask;

            return new SocketPair(connectSocket, acceptedSocket);
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class SocketPair : IDisposable
    {
        public SocketPair(Socket proxySide, Socket peerSide)
        {
            ProxySide = proxySide;
            PeerSide = peerSide;
        }

        public Socket ProxySide { get; }
        public Socket PeerSide { get; }

        public void Dispose()
        {
            try
            {
                ProxySide.Dispose();
            }
            finally
            {
                PeerSide.Dispose();
            }
        }
    }

    private sealed class NullLogger : ILogger
    {
        public void LogInfo(string message) { }
        public void LogError(string message) { }
        public void LogWarning(string message) { }
        public void LogConnect(string message) { }
        public void LogCritical(string message) { }
    }
}
