namespace TinyProxy.Tests.Protocol;

public sealed class SocksUpstreamProxyTests
{
    [Fact]
    public async Task ConnectAsync_Socks4_DomainTarget_UsesSocks4aFrame()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;

        var config = new UpstreamProxyConfig
        {
            Host = endpoint.Address.ToString(),
            Port = (ushort)endpoint.Port,
            Type = UpstreamProxyType.Socks4
        };
        var proxy = new SocksUpstreamProxy(new NullLogger(), config, TimeSpan.FromSeconds(5));

        var serverTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptSocketAsync(cts.Token);
            var request = await ReadSocks4aRequestAsync(server, cts.Token);

            Assert.Equal((byte)4, request.Version);
            Assert.Equal((byte)1, request.Command);
            Assert.Equal((ushort)443, request.Port);
            Assert.Equal(new byte[] { 0, 0, 0, 1 }, request.DestinationIp);
            Assert.Equal(string.Empty, request.UserId);
            Assert.Equal("example.com", request.Host);

            await server.SendAllAsync(new byte[] { 0, 90, 0, 0, 0, 0, 0, 0 }, cts.Token);
            server.Shutdown(SocketShutdown.Both);
        }, cts.Token);

        using var upstreamSocket = await proxy.ConnectAsync("example.com", 443, cts.Token);

        await serverTask;
        Assert.NotNull(upstreamSocket);
    }

    [Fact]
    public async Task ConnectAsync_Socks4_WithUsername_PreservesUseridAndUsesSocks4aFrame()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;

        var config = new UpstreamProxyConfig
        {
            Host = endpoint.Address.ToString(),
            Port = (ushort)endpoint.Port,
            Type = UpstreamProxyType.Socks4,
            Username = "alice"
        };
        var proxy = new SocksUpstreamProxy(new NullLogger(), config, TimeSpan.FromSeconds(5));

        var serverTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptSocketAsync(cts.Token);
            var request = await ReadSocks4aRequestAsync(server, cts.Token);

            Assert.Equal((byte)4, request.Version);
            Assert.Equal((byte)1, request.Command);
            Assert.Equal((ushort)1080, request.Port);
            Assert.Equal(new byte[] { 0, 0, 0, 1 }, request.DestinationIp);
            Assert.Equal("alice", request.UserId);
            Assert.Equal("legacy.example", request.Host);

            await server.SendAllAsync(new byte[] { 0, 90, 0, 0, 0, 0, 0, 0 }, cts.Token);
            server.Shutdown(SocketShutdown.Both);
        }, cts.Token);

        using var upstreamSocket = await proxy.ConnectAsync("legacy.example", 1080, cts.Token);

        await serverTask;
        Assert.NotNull(upstreamSocket);
    }

    [Fact]
    public async Task ConnectAsync_Socks5HandshakeFailure_ClosesSocket()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;

        var config = new UpstreamProxyConfig
        {
            Host = endpoint.Address.ToString(),
            Port = (ushort)endpoint.Port,
            Type = UpstreamProxyType.Socks5
        };
        var proxy = new SocksUpstreamProxy(new NullLogger(), config, TimeSpan.FromSeconds(5));

        var closureObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var serverTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptSocketAsync(cts.Token);
            var greeting = await ReceiveExactlyAsync(server, 3, cts.Token);

            Assert.Equal((byte)5, greeting[0]);
            Assert.Equal((byte)1, greeting[1]);
            Assert.Equal((byte)0, greeting[2]);

            await server.SendAllAsync(new byte[] { 5, 0xFF }, cts.Token);

            var buffer = new byte[1];
            try
            {
                using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, readTimeout.Token);
                var read = await server.ReceiveAsync(buffer, SocketFlags.None, linked.Token);
                closureObserved.TrySetResult(read == 0);
            }
            catch (OperationCanceledException)
            {
                closureObserved.TrySetResult(false);
            }
        }, cts.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => proxy.ConnectAsync("example.com", 443, cts.Token).AsTask());

        Assert.True(await closureObserved.Task);
        await serverTask;
    }

    private static async Task<Socks4aRequest> ReadSocks4aRequestAsync(Socket socket, CancellationToken token)
    {
        var header = await ReceiveExactlyAsync(socket, 8, token);
        var userIdBytes = await ReadNullTerminatedAsync(socket, token);
        var hostBytes = await ReadNullTerminatedAsync(socket, token);

        var port = (ushort)((header[2] << 8) | header[3]);

        return new Socks4aRequest(
            header[0],
            header[1],
            port,
            header[4..8],
            Encoding.ASCII.GetString(userIdBytes),
            Encoding.ASCII.GetString(hostBytes));
    }

    private static async Task<byte[]> ReadNullTerminatedAsync(Socket socket, CancellationToken token)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];

        while (true)
        {
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None, token);
            if (read == 0) throw new EndOfStreamException("Socket closed before null terminator.");
            if (buffer[0] == 0) return bytes.ToArray();
            bytes.Add(buffer[0]);
        }
    }

    private static async Task<byte[]> ReceiveExactlyAsync(Socket socket, int length, CancellationToken token)
    {
        var buffer = new byte[length];
        var offset = 0;

        while (offset < length)
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None, token);
            if (read == 0) throw new EndOfStreamException("Socket closed before expected bytes were received.");
            offset += read;
        }

        return buffer;
    }

    private sealed record Socks4aRequest(
        byte Version,
        byte Command,
        ushort Port,
        byte[] DestinationIp,
        string UserId,
        string Host);

    private sealed class NullLogger : ILogger
    {
        public void LogInfo(string message) { }
        public void LogError(string message) { }
        public void LogWarning(string message) { }
        public void LogConnect(string message) { }
        public void LogCritical(string message) { }
    }
}
