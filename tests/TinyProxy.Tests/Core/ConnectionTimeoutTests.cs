namespace TinyProxy.Tests.Core;

public class ConnectionTimeoutTests
{
    [Fact]
    public async Task ProcessAsync_ResponseHeadersIdleTimeout_ReturnsInternalServerError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var backendListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        backendListener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        backendListener.Listen(1);
        var backendPort = ((IPEndPoint)backendListener.LocalEndPoint!).Port;

        var backendTask = Task.Run(async () =>
        {
            using var backend = await backendListener.AcceptAsync(cts.Token);
            await ReadHeadersAsync(backend, cts.Token);

            var partialResponse = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n");
            await backend.SendAllAsync(partialResponse, cts.Token);

            // Stall longer than proxy timeout so forwarding path must fail on idle.
            await Task.Delay(TimeSpan.FromMilliseconds(600), cts.Token);
        }, cts.Token);

        var config = Configuration.Default with
        {
            Verbose = false,
            Timeout = TimeSpan.FromMilliseconds(200)
        };

        var response = await SendRequestAsync(
            config,
            $"GET http://127.0.0.1:{backendPort}/stall HTTP/1.1\r\nHost: 127.0.0.1:{backendPort}\r\nConnection: close\r\n\r\n",
            shutdownClientSend: true,
            cts.Token);

        await backendTask;

        Assert.Contains("500 Internal Server Error", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_RequestBodyIdleTimeout_ReturnsInternalServerError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var backendListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        backendListener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        backendListener.Listen(1);
        var backendPort = ((IPEndPoint)backendListener.LocalEndPoint!).Port;

        var backendTask = Task.Run(async () =>
        {
            using var backend = await backendListener.AcceptAsync(cts.Token);
            await ReadHeadersAsync(backend, cts.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(600), cts.Token);
        }, cts.Token);

        var config = Configuration.Default with
        {
            Verbose = false,
            Timeout = TimeSpan.FromMilliseconds(200)
        };

        var stalledRequest =
            $"POST http://127.0.0.1:{backendPort}/upload HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{backendPort}\r\n" +
            "Content-Length: 6\r\n" +
            "Connection: close\r\n\r\n" +
            "abc";

        var response = await SendRequestAsync(
            config,
            stalledRequest,
            shutdownClientSend: false,
            cts.Token);

        await backendTask;

        Assert.Contains("500 Internal Server Error", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_ResponseBodyIdleTimeout_AfterPartialForward_DoesNotAppendProxyError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var backendListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        backendListener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        backendListener.Listen(1);
        var backendPort = ((IPEndPoint)backendListener.LocalEndPoint!).Port;

        var backendTask = Task.Run(async () =>
        {
            using var backend = await backendListener.AcceptAsync(cts.Token);
            await ReadHeadersAsync(backend, cts.Token);

            var partialResponse = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 10\r\nConnection: close\r\n\r\nhello");
            await backend.SendAllAsync(partialResponse, cts.Token);

            // Keep connection open without remaining body bytes to trigger proxy idle timeout.
            await Task.Delay(TimeSpan.FromMilliseconds(600), cts.Token);
        }, cts.Token);

        var config = Configuration.Default with
        {
            Verbose = false,
            Timeout = TimeSpan.FromMilliseconds(200)
        };

        var response = await SendRequestAsync(
            config,
            $"GET http://127.0.0.1:{backendPort}/partial HTTP/1.1\r\nHost: 127.0.0.1:{backendPort}\r\nConnection: close\r\n\r\n",
            shutdownClientSend: true,
            cts.Token);

        await backendTask;

        Assert.StartsWith("HTTP/1.1 200 OK", response, StringComparison.Ordinal);
        Assert.DoesNotContain("500 Internal Server Error", response, StringComparison.Ordinal);
        Assert.DoesNotContain("504 Gateway Timeout", response, StringComparison.Ordinal);
        Assert.Equal(-1, response.IndexOf("HTTP/1.1", response.IndexOf("HTTP/1.1", StringComparison.Ordinal) + 1, StringComparison.Ordinal));
    }

    private static async Task<string> SendRequestAsync(
        Configuration config,
        string rawRequest,
        bool shutdownClientSend,
        CancellationToken cancellationToken)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!, cancellationToken);
        using var server = await listener.AcceptAsync(cancellationToken);
        await connectTask;

        var logger = new NullLogger();
        var stats = new Stats();
        using var accessLogger = new AccessLogger(config, logger);
        var loopDetector = new LoopDetector();
        using var connection = new Connection(server, logger, config, stats, accessLogger, loopDetector);

        var requestBytes = Encoding.ASCII.GetBytes(rawRequest);
        await client.SendAsync(requestBytes, SocketFlags.None, cancellationToken);

        if (shutdownClientSend)
            client.Shutdown(SocketShutdown.Send);

        await connection.ProcessAsync();
        connection.Dispose();

        if (!shutdownClientSend)
        {
            try
            {
                client.Shutdown(SocketShutdown.Send);
            }
            catch (SocketException)
            {
                // Socket already closed by proxy.
            }
        }

        var buffer = new byte[4096];
        using var ms = new MemoryStream();

        while (true)
        {
            int read;
            try
            {
                read = await client.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.OperationAborted)
            {
                break;
            }

            if (read <= 0) break;
            ms.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task ReadHeadersAsync(Socket socket, CancellationToken token)
    {
        var buffer = new byte[4096];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(totalRead), SocketFlags.None, token);
            if (read <= 0) return;

            totalRead += read;
            if (ContainsHeadersEnd(buffer.AsSpan(0, totalRead)))
                return;
        }
    }

    private static bool ContainsHeadersEnd(ReadOnlySpan<byte> buffer)
    {
        for (var i = 0; i < buffer.Length - 1; i++)
        {
            if (i < buffer.Length - 3 &&
                buffer[i] == '\r' &&
                buffer[i + 1] == '\n' &&
                buffer[i + 2] == '\r' &&
                buffer[i + 3] == '\n')
                return true;

            if (buffer[i] == '\n' && buffer[i + 1] == '\n')
                return true;
        }

        return false;
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
