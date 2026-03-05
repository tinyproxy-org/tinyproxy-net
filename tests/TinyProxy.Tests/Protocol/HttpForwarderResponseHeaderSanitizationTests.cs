namespace TinyProxy.Tests.Protocol;

public class HttpForwarderResponseHeaderSanitizationTests
{
    private static readonly MethodInfo s_forwardResponseAsyncMethod =
        typeof(HttpForwarder).GetMethod(
            "ForwardResponseAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public async Task ForwardResponseAsync_RemovesConnectionOptionHeaders_ForNonUpgradeResponses()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var upstream = await CreateConnectedSocketsAsync(cts.Token);
        using var downstream = await CreateConnectedSocketsAsync(cts.Token);

        var forwarder = CreateForwarder(addViaHeader: false);
        var forwardTask = InvokeForwardResponseAsync(
            forwarder,
            upstream.ProxySide,
            downstream.ProxySide,
            TinyProxy.Protocol.Http.HttpMethod.Get,
            "HTTP/1.1",
            null,
            cts.Token);

        var responseBytes =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Length: 5\r\n" +
            "Connection: keep-alive, X-Hop\r\n" +
            "Proxy-Authenticate: Basic realm=\"x\"\r\n" +
            "X-Hop: secret\r\n" +
            "Server: origin\r\n\r\nhello";
        var expected =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Length: 5\r\n" +
            "Server: origin\r\n\r\nhello";

        await upstream.PeerSide.SendAllAsync(Encoding.ASCII.GetBytes(responseBytes), cts.Token);
        var received = await ReceiveExactlyAsync(downstream.PeerSide, Encoding.ASCII.GetByteCount(expected), cts.Token);
        var text = Encoding.ASCII.GetString(received);

        Assert.Equal(expected, text);

        await forwardTask;
    }

    [Fact]
    public async Task ForwardResponseAsync_AppendsViaHeader_WhenEnabled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var upstream = await CreateConnectedSocketsAsync(cts.Token);
        using var downstream = await CreateConnectedSocketsAsync(cts.Token);

        var forwarder = CreateForwarder(addViaHeader: true, viaProxyName: "proxy-edge");
        var forwardTask = InvokeForwardResponseAsync(
            forwarder,
            upstream.ProxySide,
            downstream.ProxySide,
            TinyProxy.Protocol.Http.HttpMethod.Get,
            "HTTP/1.0",
            null,
            cts.Token);

        var responseBytes =
            "HTTP/1.1 204 No Content\r\n" +
            "Connection: close\r\n" +
            "Via: 1.0 upstream\r\n\r\n";
        var expected =
            "HTTP/1.1 204 No Content\r\n" +
            "Via: 1.0 upstream, 1.0 proxy-edge\r\n\r\n";

        await upstream.PeerSide.SendAllAsync(Encoding.ASCII.GetBytes(responseBytes), cts.Token);
        var received = await ReceiveExactlyAsync(downstream.PeerSide, Encoding.ASCII.GetByteCount(expected), cts.Token);
        var text = Encoding.ASCII.GetString(received);

        Assert.Equal(expected, text);

        await forwardTask;
    }

    [Fact]
    public async Task ForwardResponseAsync_OmitsHeaders_ForHttp09Requests()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var upstream = await CreateConnectedSocketsAsync(cts.Token);
        using var downstream = await CreateConnectedSocketsAsync(cts.Token);

        var forwarder = CreateForwarder(addViaHeader: false);
        var forwardTask = InvokeForwardResponseAsync(
            forwarder,
            upstream.ProxySide,
            downstream.ProxySide,
            TinyProxy.Protocol.Http.HttpMethod.Get,
            "HTTP/0.9",
            null,
            cts.Token);

        var responseBytes =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Length: 5\r\n" +
            "Server: origin\r\n\r\nhello";
        var expected = "hello";

        await upstream.PeerSide.SendAllAsync(Encoding.ASCII.GetBytes(responseBytes), cts.Token);
        var received = await ReceiveExactlyAsync(downstream.PeerSide, Encoding.ASCII.GetByteCount(expected), cts.Token);
        var text = Encoding.ASCII.GetString(received);

        Assert.Equal(expected, text);

        await forwardTask;
    }

    [Fact]
    public async Task ForwardResponseAsync_PreservesFoldedResponseHeaders()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var upstream = await CreateConnectedSocketsAsync(cts.Token);
        using var downstream = await CreateConnectedSocketsAsync(cts.Token);

        var forwarder = CreateForwarder(addViaHeader: false);
        var forwardTask = InvokeForwardResponseAsync(
            forwarder,
            upstream.ProxySide,
            downstream.ProxySide,
            TinyProxy.Protocol.Http.HttpMethod.Get,
            "HTTP/1.1",
            null,
            cts.Token);

        var responseBytes =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Length: 5\r\n" +
            "X-Test: one\r\n" +
            "\ttwo\r\n" +
            "Connection: close\r\n\r\nhello";
        var expected =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Length: 5\r\n" +
            "X-Test: one two\r\n\r\nhello";

        await upstream.PeerSide.SendAllAsync(Encoding.ASCII.GetBytes(responseBytes), cts.Token);
        var received = await ReceiveExactlyAsync(downstream.PeerSide, Encoding.ASCII.GetByteCount(expected), cts.Token);
        var text = Encoding.ASCII.GetString(received);

        Assert.Equal(expected, text);

        await forwardTask;
    }

    [Fact]
    public async Task ForwardResponseAsync_RewritesLocationHeader_WhenReverseBaseUrlConfigured()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var upstream = await CreateConnectedSocketsAsync(cts.Token);
        using var downstream = await CreateConnectedSocketsAsync(cts.Token);

        var config = Configuration.Default with
        {
            Verbose = false,
            AddViaHeader = false,
            ReverseBaseUrl = "https://proxy.example/",
            ReversePaths = new List<ReversePathConfig>
            {
                new() { Path = "/app/", Url = "http://backend.internal/base/" }
            }
        };

        var forwarder = CreateForwarder(config);
        var forwardTask = InvokeForwardResponseAsync(
            forwarder,
            upstream.ProxySide,
            downstream.ProxySide,
            TinyProxy.Protocol.Http.HttpMethod.Get,
            "HTTP/1.1",
            null,
            cts.Token);

        var responseBytes =
            "HTTP/1.1 302 Found\r\n" +
            "Location: http://backend.internal/base/login\r\n" +
            "Content-Length: 0\r\n" +
            "Connection: close\r\n\r\n";
        var expected =
            "HTTP/1.1 302 Found\r\n" +
            "Location: https://proxy.example/app/login\r\n" +
            "Content-Length: 0\r\n\r\n";

        await upstream.PeerSide.SendAllAsync(Encoding.ASCII.GetBytes(responseBytes), cts.Token);
        var received = await ReceiveExactlyAsync(downstream.PeerSide, Encoding.ASCII.GetByteCount(expected), cts.Token);
        var text = Encoding.ASCII.GetString(received);

        Assert.Equal(expected, text);

        await forwardTask;
    }

    [Fact]
    public async Task ForwardResponseAsync_AddsReverseMagicCookie_WhenEnabledAndPathPresent()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var upstream = await CreateConnectedSocketsAsync(cts.Token);
        using var downstream = await CreateConnectedSocketsAsync(cts.Token);

        var config = Configuration.Default with
        {
            Verbose = false,
            AddViaHeader = false,
            ReverseMagicEnabled = true
        };

        var forwarder = CreateForwarder(config);
        var forwardTask = InvokeForwardResponseAsync(
            forwarder,
            upstream.ProxySide,
            downstream.ProxySide,
            TinyProxy.Protocol.Http.HttpMethod.Get,
            "HTTP/1.1",
            "/app/",
            cts.Token);

        var responseBytes =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Length: 0\r\n" +
            "Connection: close\r\n\r\n";
        var expected =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Length: 0\r\n" +
            "Set-Cookie: yummy_magical_cookie=/app/; path=/\r\n\r\n";

        await upstream.PeerSide.SendAllAsync(Encoding.ASCII.GetBytes(responseBytes), cts.Token);
        var received = await ReceiveExactlyAsync(downstream.PeerSide, Encoding.ASCII.GetByteCount(expected), cts.Token);
        var text = Encoding.ASCII.GetString(received);

        Assert.Equal(expected, text);

        await forwardTask;
    }

    private static HttpForwarder CreateForwarder(bool addViaHeader, string? viaProxyName = null)
    {
        var config = Configuration.Default with
        {
            Verbose = false,
            AddViaHeader = addViaHeader,
            ViaProxyName = viaProxyName
        };

        return new HttpForwarder(
            new NullLogger(),
            config,
            new Stats(),
            new AccessLogger(config, new NullLogger()),
            "127.0.0.1");
    }

    private static HttpForwarder CreateForwarder(Configuration config)
    {
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
            var read = await socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None, token);
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
