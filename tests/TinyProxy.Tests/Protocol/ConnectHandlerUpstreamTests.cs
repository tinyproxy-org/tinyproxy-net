namespace TinyProxy.Tests.Protocol;

public class ConnectHandlerUpstreamTests
{
    private static readonly MethodInfo s_buildHttpUpstreamConnectRequestMethod =
        typeof(ConnectHandler).GetMethod(
            "BuildHttpUpstreamConnectRequest",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public void BuildHttpUpstreamConnectRequest_WithUpstreamCredentials_DropsClientAndAddsUpstreamProxyAuthorization()
    {
        var config = Configuration.Default with
        {
            Verbose = false,
            AddViaHeader = true,
            ViaProxyName = "proxy-edge"
        };
        var logger = new NullLogger();
        var stats = new Stats();
        using var accessLogger = new AccessLogger(config, logger);
        var handler = new ConnectHandler(logger, config, stats, accessLogger, "192.0.2.10");

        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Connect,
            Uri = "target.example:443",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("target.example:443"u8.ToArray()),
                ["Connection"] = new ReadOnlySequence<byte>("keep-alive, X-Hop-By-Hop"u8.ToArray()),
                ["Proxy-Authorization"] = new ReadOnlySequence<byte>("Basic Y2xpZW50OmNyZWRz"u8.ToArray()),
                ["X-Hop-By-Hop"] = new ReadOnlySequence<byte>("drop-me"u8.ToArray()),
                ["X-Hop-By-Hop-2"] = new ReadOnlySequence<byte>("drop-me-2"u8.ToArray()),
                ["User-Agent"] = new ReadOnlySequence<byte>("tinyproxy-test"u8.ToArray()),
                ["Via"] = new ReadOnlySequence<byte>("1.0 upstream-proxy"u8.ToArray())
            },
            HeaderLines = new[]
            {
                new KeyValuePair<string, ReadOnlySequence<byte>>("Host", new ReadOnlySequence<byte>("target.example:443"u8.ToArray())),
                new KeyValuePair<string, ReadOnlySequence<byte>>("X-Dup", new ReadOnlySequence<byte>("one"u8.ToArray())),
                new KeyValuePair<string, ReadOnlySequence<byte>>("X-Dup", new ReadOnlySequence<byte>("two"u8.ToArray())),
                new KeyValuePair<string, ReadOnlySequence<byte>>("Connection", new ReadOnlySequence<byte>("keep-alive, X-Hop-By-Hop"u8.ToArray())),
                new KeyValuePair<string, ReadOnlySequence<byte>>("Connection", new ReadOnlySequence<byte>("X-Hop-By-Hop-2"u8.ToArray())),
                new KeyValuePair<string, ReadOnlySequence<byte>>("Proxy-Authorization", new ReadOnlySequence<byte>("Basic Y2xpZW50OmNyZWRz"u8.ToArray())),
                new KeyValuePair<string, ReadOnlySequence<byte>>("X-Hop-By-Hop", new ReadOnlySequence<byte>("drop-me"u8.ToArray())),
                new KeyValuePair<string, ReadOnlySequence<byte>>("X-Hop-By-Hop-2", new ReadOnlySequence<byte>("drop-me-2"u8.ToArray())),
                new KeyValuePair<string, ReadOnlySequence<byte>>("User-Agent", new ReadOnlySequence<byte>("tinyproxy-test"u8.ToArray())),
                new KeyValuePair<string, ReadOnlySequence<byte>>("Via", new ReadOnlySequence<byte>("1.0 upstream-proxy"u8.ToArray()))
            }
        };

        var upstream = new UpstreamProxyConfig
        {
            Host = "upstream.local",
            Port = 3128,
            Username = "alice",
            Password = "secret",
            Type = UpstreamProxyType.Http
        };

        var requestBytes = (byte[])s_buildHttpUpstreamConnectRequestMethod.Invoke(
            handler,
            new object?[] { request, "target.example", 443, upstream })!;

        var text = Encoding.ASCII.GetString(requestBytes);

        Assert.StartsWith("CONNECT target.example:443 HTTP/1.1\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Host: target.example\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Host: target.example:443\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Connection: close\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Connection: keep-alive", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Proxy-Authorization: Basic YWxpY2U6c2VjcmV0\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Proxy-Authorization: Basic Y2xpZW50OmNyZWRz\r\n", text, StringComparison.Ordinal);
        Assert.Contains("User-Agent: tinyproxy-test\r\n", text, StringComparison.Ordinal);
        Assert.Contains("X-Dup: one\r\n", text, StringComparison.Ordinal);
        Assert.Contains("X-Dup: two\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Hop-By-Hop:", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Hop-By-Hop-2:", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Via: 1.0 upstream-proxy, 1.1 proxy-edge\r\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHttpUpstreamConnectRequest_NormalizesTrailingHttpVersionToken_LikeTinyproxySscanf()
    {
        var config = Configuration.Default with
        {
            Verbose = false,
            AddViaHeader = true,
            ViaProxyName = "proxy-edge"
        };
        var logger = new NullLogger();
        var stats = new Stats();
        using var accessLogger = new AccessLogger(config, logger);
        var handler = new ConnectHandler(logger, config, stats, accessLogger, "192.0.2.10");

        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Connect,
            Uri = "target.example:443",
            Version = "HTTP/1.3beta",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("target.example:443"u8.ToArray())
            }
        };

        var upstream = new UpstreamProxyConfig
        {
            Host = "upstream.local",
            Port = 3128,
            Type = UpstreamProxyType.Http
        };

        var requestBytes = (byte[])s_buildHttpUpstreamConnectRequestMethod.Invoke(
            handler,
            new object?[] { request, "target.example", 443, upstream })!;

        var text = Encoding.ASCII.GetString(requestBytes);
        Assert.StartsWith("CONNECT target.example:443 HTTP/1.3\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Via: 1.3 proxy-edge\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHttpUpstreamConnectRequest_DropsClientProxyAuthorization_WhenLocalBasicAuthDisabledAndNoUpstreamCredentials()
    {
        var config = Configuration.Default with
        {
            Verbose = false,
            AddViaHeader = false
        };
        var logger = new NullLogger();
        var stats = new Stats();
        using var accessLogger = new AccessLogger(config, logger);
        var handler = new ConnectHandler(logger, config, stats, accessLogger, "192.0.2.10");

        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Connect,
            Uri = "target.example:443",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("target.example:443"u8.ToArray()),
                ["Proxy-Authorization"] = new ReadOnlySequence<byte>("Basic Y2xpZW50OmNyZWRz"u8.ToArray())
            },
            HeaderLines = new[]
            {
                new KeyValuePair<string, ReadOnlySequence<byte>>("Host", new ReadOnlySequence<byte>("target.example:443"u8.ToArray())),
                new KeyValuePair<string, ReadOnlySequence<byte>>("Proxy-Authorization", new ReadOnlySequence<byte>("Basic Y2xpZW50OmNyZWRz"u8.ToArray()))
            }
        };

        var upstream = new UpstreamProxyConfig
        {
            Host = "upstream.local",
            Port = 3128,
            Type = UpstreamProxyType.Http
        };

        var requestBytes = (byte[])s_buildHttpUpstreamConnectRequestMethod.Invoke(
            handler,
            new object?[] { request, "target.example", 443, upstream })!;

        var text = Encoding.ASCII.GetString(requestBytes);
        Assert.DoesNotContain("Proxy-Authorization: Basic Y2xpZW50OmNyZWRz\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHttpUpstreamConnectRequest_FiltersCustomHeaders_WhenAnonymousModeEnabled()
    {
        var config = Configuration.Default with
        {
            Verbose = false,
            AddViaHeader = false,
            AnonymousAllowedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "X-Custom-Allow"
            },
            CustomHeaders = new List<HttpHeader>
            {
                new() { Name = "X-Custom-Allow", Value = "allowed" },
                new() { Name = "X-Custom-Drop", Value = "blocked" }
            }
        };
        var logger = new NullLogger();
        var stats = new Stats();
        using var accessLogger = new AccessLogger(config, logger);
        var handler = new ConnectHandler(logger, config, stats, accessLogger, "192.0.2.10");

        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Connect,
            Uri = "target.example:443",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("target.example:443"u8.ToArray())
            }
        };

        var upstream = new UpstreamProxyConfig
        {
            Host = "upstream.local",
            Port = 3128,
            Type = UpstreamProxyType.Http
        };

        var requestBytes = (byte[])s_buildHttpUpstreamConnectRequestMethod.Invoke(
            handler,
            new object?[] { request, "target.example", 443, upstream })!;

        var text = Encoding.ASCII.GetString(requestBytes);
        Assert.Contains("X-Custom-Allow: allowed\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Custom-Drop: blocked\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHttpUpstreamConnectRequest_IgnoresConflictingCustomHopByHopHeaders()
    {
        var config = Configuration.Default with
        {
            Verbose = false,
            AddViaHeader = false,
            CustomHeaders = new List<HttpHeader>
            {
                new() { Name = "Host", Value = "evil.example" },
                new() { Name = "Connection", Value = "keep-alive" },
                new() { Name = "Proxy-Authorization", Value = "Basic ZXZpbDpldmls" },
                new() { Name = "X-Custom-Allow", Value = "allowed" }
            }
        };
        var logger = new NullLogger();
        var stats = new Stats();
        using var accessLogger = new AccessLogger(config, logger);
        var handler = new ConnectHandler(logger, config, stats, accessLogger, "192.0.2.10");

        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Connect,
            Uri = "target.example:443",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("target.example:443"u8.ToArray())
            }
        };

        var upstream = new UpstreamProxyConfig
        {
            Host = "upstream.local",
            Port = 3128,
            Type = UpstreamProxyType.Http
        };

        var requestBytes = (byte[])s_buildHttpUpstreamConnectRequestMethod.Invoke(
            handler,
            new object?[] { request, "target.example", 443, upstream })!;

        var text = Encoding.ASCII.GetString(requestBytes);
        Assert.Contains("Host: target.example\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Connection: close\r\n", text, StringComparison.Ordinal);
        Assert.Contains("X-Custom-Allow: allowed\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Host: evil.example\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Connection: keep-alive\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Proxy-Authorization: Basic ZXZpbDpldmls\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleConnectAsync_HttpUpstreamProxy_RelaysTunnelData()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var downstream = await CreateConnectedSocketsAsync(cts.Token);
        using var upstreamListener = new TcpListener(IPAddress.Loopback, 0);
        upstreamListener.Start();

        var upstreamEndpoint = (IPEndPoint)upstreamListener.LocalEndpoint;
        var config = Configuration.Default with
        {
            Verbose = false,
            AddViaHeader = true,
            ViaProxyName = "proxy-edge",
            UpstreamProxy = new UpstreamProxyConfig
            {
                Host = upstreamEndpoint.Address.ToString(),
                Port = (ushort)upstreamEndpoint.Port,
                Type = UpstreamProxyType.Http,
                Username = "alice",
                Password = "secret"
            }
        };

        var logger = new NullLogger();
        var stats = new Stats();
        using var accessLogger = new AccessLogger(config, logger);
        var loopDetector = new LoopDetector();
        using var connection = new Connection(downstream.ProxySide, logger, config, stats, accessLogger, loopDetector);

        var handler = new ConnectHandler(logger, config, stats, accessLogger, "127.0.0.1", loopDetector);
        var connectRequest = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Connect,
            Uri = "target.example:443",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("target.example:443"u8.ToArray()),
                ["User-Agent"] = new ReadOnlySequence<byte>("agent-from-client"u8.ToArray())
            },
            HeaderLines = new[]
            {
                new KeyValuePair<string, ReadOnlySequence<byte>>("Host", new ReadOnlySequence<byte>("target.example:443"u8.ToArray())),
                new KeyValuePair<string, ReadOnlySequence<byte>>("X-Dup", new ReadOnlySequence<byte>("one"u8.ToArray())),
                new KeyValuePair<string, ReadOnlySequence<byte>>("X-Dup", new ReadOnlySequence<byte>("two"u8.ToArray())),
                new KeyValuePair<string, ReadOnlySequence<byte>>("User-Agent", new ReadOnlySequence<byte>("agent-from-client"u8.ToArray()))
            }
        };

        var upstreamTask = Task.Run(async () =>
        {
            using var upstreamSocket = await upstreamListener.AcceptSocketAsync(cts.Token);

            var upstreamConnectRequest = await ReadHeadersAsStringAsync(upstreamSocket, cts.Token);
            Assert.Contains("CONNECT target.example:443 HTTP/1.1\r\n", upstreamConnectRequest, StringComparison.Ordinal);
            Assert.Contains("Host: target.example\r\n", upstreamConnectRequest, StringComparison.Ordinal);
            Assert.Contains("Proxy-Authorization: Basic YWxpY2U6c2VjcmV0\r\n", upstreamConnectRequest, StringComparison.Ordinal);
            Assert.Contains("User-Agent: agent-from-client\r\n", upstreamConnectRequest, StringComparison.Ordinal);
            Assert.Contains("X-Dup: one\r\n", upstreamConnectRequest, StringComparison.Ordinal);
            Assert.Contains("X-Dup: two\r\n", upstreamConnectRequest, StringComparison.Ordinal);

            var connectOk =
                "HTTP/1.1 200 Connection established\r\n" +
                "Connection: keep-alive, X-Hop\r\n" +
                "X-Hop: remove-me\r\n" +
                "Proxy-Authenticate: Basic realm=\"upstream\"\r\n" +
                "Via: 1.0 upstream-gw\r\n\r\n";
            await upstreamSocket.SendAllAsync(Encoding.ASCII.GetBytes(connectOk), cts.Token);

            var payload = await ReceiveExactlyAsync(upstreamSocket, 4, cts.Token);
            Assert.Equal("ping"u8.ToArray(), payload);

            await upstreamSocket.SendAllAsync(payload, cts.Token);
            upstreamSocket.Shutdown(SocketShutdown.Both);
        }, cts.Token);

        var handlerTask = handler.HandleConnectAsync(
            connection,
            connectRequest,
            ReadOnlySequence<byte>.Empty,
            cts.Token).AsTask();

        var establishedResponse = await ReadHeadersAsStringAsync(downstream.PeerSide, cts.Token);
        Assert.Contains("200 Connection established", establishedResponse, StringComparison.Ordinal);
        Assert.Contains("Via: 1.0 upstream-gw, 1.1 proxy-edge\r\n", establishedResponse, StringComparison.Ordinal);
        Assert.DoesNotContain("Connection:", establishedResponse, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Hop:", establishedResponse, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Proxy-Authenticate:", establishedResponse, StringComparison.OrdinalIgnoreCase);

        await downstream.PeerSide.SendAllAsync("ping"u8.ToArray(), cts.Token);
        var echoed = await ReceiveExactlyAsync(downstream.PeerSide, 4, cts.Token);
        Assert.Equal("ping"u8.ToArray(), echoed);

        downstream.PeerSide.Shutdown(SocketShutdown.Send);

        await upstreamTask;
        await handlerTask;
    }

    [Fact]
    public async Task HandleConnectAsync_HttpUpstreamProxy_ForwardsNon200Response()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var downstream = await CreateConnectedSocketsAsync(cts.Token);
        using var upstreamListener = new TcpListener(IPAddress.Loopback, 0);
        upstreamListener.Start();

        var upstreamEndpoint = (IPEndPoint)upstreamListener.LocalEndpoint;
        var config = Configuration.Default with
        {
            Verbose = false,
            AddViaHeader = true,
            ViaProxyName = "proxy-edge",
            UpstreamProxy = new UpstreamProxyConfig
            {
                Host = upstreamEndpoint.Address.ToString(),
                Port = (ushort)upstreamEndpoint.Port,
                Type = UpstreamProxyType.Http
            }
        };

        var logger = new NullLogger();
        var stats = new Stats();
        using var accessLogger = new AccessLogger(config, logger);
        var loopDetector = new LoopDetector();
        using var connection = new Connection(downstream.ProxySide, logger, config, stats, accessLogger, loopDetector);
        var handler = new ConnectHandler(logger, config, stats, accessLogger, "127.0.0.1", loopDetector);

        var connectRequest = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Connect,
            Uri = "target.example:443",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("target.example:443"u8.ToArray())
            }
        };

        var upstreamTask = Task.Run(async () =>
        {
            using var upstreamSocket = await upstreamListener.AcceptSocketAsync(cts.Token);
            _ = await ReadHeadersAsStringAsync(upstreamSocket, cts.Token);

            var response =
                "HTTP/1.1 407 Proxy Authentication Required\r\n" +
                "Connection: keep-alive, X-Hop\r\n" +
                "X-Hop: remove-me\r\n" +
                "Keep-Alive: timeout=5\r\n" +
                "Proxy-Authenticate: Basic realm=\"upstream\"\r\n" +
                "Via: 1.0 upstream-gw\r\n" +
                "Content-Length: 5\r\n\r\nerror";
            await upstreamSocket.SendAllAsync(Encoding.ASCII.GetBytes(response), cts.Token);
            upstreamSocket.Shutdown(SocketShutdown.Send);
        }, cts.Token);

        var handlerTask = handler.HandleConnectAsync(
            connection,
            connectRequest,
            ReadOnlySequence<byte>.Empty,
            cts.Token).AsTask();

        var (downstreamHeaders, prefetchedBody) = await ReadHeadersAndRemainingAsync(downstream.PeerSide, cts.Token);
        var bodyLength = ParseContentLength(downstreamHeaders);
        var downstreamBody = await ReceiveBodyWithPrefetchedAsync(downstream.PeerSide, prefetchedBody, bodyLength, cts.Token);
        var responseText = downstreamHeaders + Encoding.ASCII.GetString(downstreamBody);

        Assert.Contains("407 Proxy Authentication Required", responseText, StringComparison.Ordinal);
        Assert.DoesNotContain("Proxy-Authenticate:", responseText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Connection:", responseText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Hop:", responseText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Keep-Alive:", responseText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Content-Length: 5\r\n", responseText, StringComparison.Ordinal);
        Assert.Contains("Via: 1.0 upstream-gw, 1.1 proxy-edge\r\n", responseText, StringComparison.Ordinal);
        Assert.EndsWith("error", responseText, StringComparison.Ordinal);
        Assert.DoesNotContain("502 Bad Gateway", responseText, StringComparison.Ordinal);

        await upstreamTask;
        await handlerTask;
    }

    [Fact]
    public async Task HandleConnectAsync_HttpUpstreamProxy_Non200ContentLength_DoesNotWaitForUpstreamClose()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var downstream = await CreateConnectedSocketsAsync(cts.Token);
        using var upstreamListener = new TcpListener(IPAddress.Loopback, 0);
        upstreamListener.Start();

        var upstreamEndpoint = (IPEndPoint)upstreamListener.LocalEndpoint;
        var config = Configuration.Default with
        {
            Verbose = false,
            UpstreamProxy = new UpstreamProxyConfig
            {
                Host = upstreamEndpoint.Address.ToString(),
                Port = (ushort)upstreamEndpoint.Port,
                Type = UpstreamProxyType.Http
            }
        };

        var logger = new NullLogger();
        var stats = new Stats();
        using var accessLogger = new AccessLogger(config, logger);
        var loopDetector = new LoopDetector();
        using var connection = new Connection(downstream.ProxySide, logger, config, stats, accessLogger, loopDetector);
        var handler = new ConnectHandler(logger, config, stats, accessLogger, "127.0.0.1", loopDetector);
        var allowClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var connectRequest = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Connect,
            Uri = "target.example:443",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("target.example:443"u8.ToArray())
            }
        };

        var upstreamTask = Task.Run(async () =>
        {
            using var upstreamSocket = await upstreamListener.AcceptSocketAsync(cts.Token);
            _ = await ReadHeadersAsStringAsync(upstreamSocket, cts.Token);

            var response =
                "HTTP/1.1 407 Proxy Authentication Required\r\n" +
                "Content-Length: 5\r\n\r\nerror";
            await upstreamSocket.SendAllAsync(Encoding.ASCII.GetBytes(response), cts.Token);

            await allowClose.Task.WaitAsync(cts.Token);
            try
            {
                upstreamSocket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
                // Connection may already be closed by proxy side.
            }
        }, cts.Token);

        var handlerTask = handler.HandleConnectAsync(
            connection,
            connectRequest,
            ReadOnlySequence<byte>.Empty,
            cts.Token).AsTask();

        var (downstreamHeaders, prefetchedBody) = await ReadHeadersAndRemainingAsync(downstream.PeerSide, cts.Token);
        var bodyLength = ParseContentLength(downstreamHeaders);
        var downstreamBody = await ReceiveBodyWithPrefetchedAsync(downstream.PeerSide, prefetchedBody, bodyLength, cts.Token);
        var responseText = downstreamHeaders + Encoding.ASCII.GetString(downstreamBody);

        Assert.Contains("407 Proxy Authentication Required", responseText, StringComparison.Ordinal);
        await handlerTask.WaitAsync(TimeSpan.FromMilliseconds(500), cts.Token);
        Assert.True(handlerTask.IsCompletedSuccessfully);

        allowClose.TrySetResult();
        await upstreamTask;
        await handlerTask;
    }

    [Fact]
    public async Task HandleConnectAsync_DirectConnect_AppliesBindSame_OnTargetConnection()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var bindSameAddress = GetAvailableLoopbackAddress();
        using var downstream = await CreateConnectedSocketsAsync(cts.Token, bindSameAddress);
        using var targetListener = new TcpListener(IPAddress.Loopback, 0);
        targetListener.Start();

        var targetEndpoint = (IPEndPoint)targetListener.LocalEndpoint;
        var config = Configuration.Default with
        {
            Verbose = false,
            BindSame = true,
            ConnectIdleTimeout = TimeSpan.FromSeconds(2)
        };

        var logger = new NullLogger();
        var stats = new Stats();
        using var accessLogger = new AccessLogger(config, logger);
        var loopDetector = new LoopDetector();
        using var connection = new Connection(downstream.ProxySide, logger, config, stats, accessLogger, loopDetector);
        var handler = new ConnectHandler(logger, config, stats, accessLogger, "127.0.0.1", loopDetector);

        var connectRequest = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Connect,
            Uri = $"{targetEndpoint.Address}:{targetEndpoint.Port}",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes($"{targetEndpoint.Address}:{targetEndpoint.Port}"))
            }
        };

        var targetTask = Task.Run(async () =>
        {
            using var targetSocket = await targetListener.AcceptSocketAsync(cts.Token);
            var remoteEndPoint = Assert.IsType<IPEndPoint>(targetSocket.RemoteEndPoint);
            Assert.Equal(bindSameAddress, remoteEndPoint.Address);

            var payload = await ReceiveExactlyAsync(targetSocket, 4, cts.Token);
            Assert.Equal("ping"u8.ToArray(), payload);
            await targetSocket.SendAllAsync(payload, cts.Token);
            targetSocket.Shutdown(SocketShutdown.Send);
        }, cts.Token);

        var handlerTask = handler.HandleConnectAsync(
            connection,
            connectRequest,
            ReadOnlySequence<byte>.Empty,
            cts.Token).AsTask();

        var establishedResponse = await ReadHeadersAsStringAsync(downstream.PeerSide, cts.Token);
        Assert.Contains("200 Connection established", establishedResponse, StringComparison.Ordinal);

        await downstream.PeerSide.SendAllAsync("ping"u8.ToArray(), cts.Token);
        downstream.PeerSide.Shutdown(SocketShutdown.Send);
        var echoed = await ReceiveExactlyAsync(downstream.PeerSide, 4, cts.Token);
        Assert.Equal("ping"u8.ToArray(), echoed);

        await targetTask;
        await handlerTask;
    }

    [Fact]
    public async Task HandleConnectAsync_HttpUpstreamProxy_AppliesBindAddresses_OnUpstreamConnection()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var downstream = await CreateConnectedSocketsAsync(cts.Token);
        using var upstreamListener = new TcpListener(IPAddress.Loopback, 0);
        upstreamListener.Start();

        var upstreamEndpoint = (IPEndPoint)upstreamListener.LocalEndpoint;
        var bindAddress = GetAvailableLoopbackAddress();
        var config = Configuration.Default with
        {
            Verbose = false,
            BindAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                bindAddress.ToString()
            },
            UpstreamProxy = new UpstreamProxyConfig
            {
                Host = upstreamEndpoint.Address.ToString(),
                Port = (ushort)upstreamEndpoint.Port,
                Type = UpstreamProxyType.Http
            }
        };

        var logger = new NullLogger();
        var stats = new Stats();
        using var accessLogger = new AccessLogger(config, logger);
        var loopDetector = new LoopDetector();
        using var connection = new Connection(downstream.ProxySide, logger, config, stats, accessLogger, loopDetector);
        var handler = new ConnectHandler(logger, config, stats, accessLogger, "127.0.0.1", loopDetector);

        var connectRequest = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Connect,
            Uri = "target.example:443",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("target.example:443"u8.ToArray())
            }
        };

        var upstreamTask = Task.Run(async () =>
        {
            using var upstreamSocket = await upstreamListener.AcceptSocketAsync(cts.Token);
            var remoteEndPoint = Assert.IsType<IPEndPoint>(upstreamSocket.RemoteEndPoint);
            Assert.Equal(bindAddress, remoteEndPoint.Address);

            var requestText = await ReadHeadersAsStringAsync(upstreamSocket, cts.Token);
            Assert.Contains("CONNECT target.example:443 HTTP/1.1\r\n", requestText, StringComparison.Ordinal);

            await upstreamSocket.SendAllAsync(
                Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"),
                cts.Token);

            var payload = await ReceiveExactlyAsync(upstreamSocket, 4, cts.Token);
            Assert.Equal("ping"u8.ToArray(), payload);
            await upstreamSocket.SendAllAsync(payload, cts.Token);
            upstreamSocket.Shutdown(SocketShutdown.Send);
        }, cts.Token);

        var handlerTask = handler.HandleConnectAsync(
            connection,
            connectRequest,
            ReadOnlySequence<byte>.Empty,
            cts.Token).AsTask();

        var establishedResponse = await ReadHeadersAsStringAsync(downstream.PeerSide, cts.Token);
        Assert.Contains("200 Connection established", establishedResponse, StringComparison.Ordinal);

        await downstream.PeerSide.SendAllAsync("ping"u8.ToArray(), cts.Token);
        downstream.PeerSide.Shutdown(SocketShutdown.Send);
        var echoed = await ReceiveExactlyAsync(downstream.PeerSide, 4, cts.Token);
        Assert.Equal("ping"u8.ToArray(), echoed);

        await upstreamTask;
        await handlerTask;
    }

    [Fact]
    public async Task HandleConnectAsync_SocksUpstreamProxy_AppliesBindSame_OnUpstreamConnection()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var bindSameAddress = GetAvailableLoopbackAddress();
        using var downstream = await CreateConnectedSocketsAsync(cts.Token, bindSameAddress);
        using var socksListener = new TcpListener(IPAddress.Loopback, 0);
        socksListener.Start();

        var socksEndpoint = (IPEndPoint)socksListener.LocalEndpoint;
        var config = Configuration.Default with
        {
            Verbose = false,
            BindSame = true,
            ConnectIdleTimeout = TimeSpan.FromSeconds(2),
            UpstreamProxy = new UpstreamProxyConfig
            {
                Host = socksEndpoint.Address.ToString(),
                Port = (ushort)socksEndpoint.Port,
                Type = UpstreamProxyType.Socks5
            }
        };

        var logger = new NullLogger();
        var stats = new Stats();
        using var accessLogger = new AccessLogger(config, logger);
        var loopDetector = new LoopDetector();
        using var connection = new Connection(downstream.ProxySide, logger, config, stats, accessLogger, loopDetector);
        var handler = new ConnectHandler(logger, config, stats, accessLogger, "127.0.0.1", loopDetector);

        var connectRequest = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Connect,
            Uri = "target.example:443",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("target.example:443"u8.ToArray())
            }
        };

        var socksTask = Task.Run(async () =>
        {
            using var socksSocket = await socksListener.AcceptSocketAsync(cts.Token);
            var remoteEndPoint = Assert.IsType<IPEndPoint>(socksSocket.RemoteEndPoint);
            Assert.Equal(bindSameAddress, remoteEndPoint.Address);

            var greeting = await ReceiveExactlyAsync(socksSocket, 3, cts.Token);
            Assert.Equal((byte)5, greeting[0]);
            Assert.Equal((byte)1, greeting[1]);
            Assert.Equal((byte)0, greeting[2]);
            await socksSocket.SendAllAsync(new byte[] { 5, 0 }, cts.Token);

            var connectHeader = await ReceiveExactlyAsync(socksSocket, 4, cts.Token);
            Assert.Equal((byte)5, connectHeader[0]);
            Assert.Equal((byte)1, connectHeader[1]);
            Assert.Equal((byte)0, connectHeader[2]);
            Assert.Equal((byte)3, connectHeader[3]);

            var hostLength = (await ReceiveExactlyAsync(socksSocket, 1, cts.Token))[0];
            var host = Encoding.ASCII.GetString(await ReceiveExactlyAsync(socksSocket, hostLength, cts.Token));
            var portBytes = await ReceiveExactlyAsync(socksSocket, 2, cts.Token);
            var port = (portBytes[0] << 8) | portBytes[1];
            Assert.Equal("target.example", host);
            Assert.Equal(443, port);

            await socksSocket.SendAllAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, cts.Token);

            var payload = await ReceiveExactlyAsync(socksSocket, 4, cts.Token);
            Assert.Equal("ping"u8.ToArray(), payload);
            await socksSocket.SendAllAsync(payload, cts.Token);
            socksSocket.Shutdown(SocketShutdown.Send);
        }, cts.Token);

        var handlerTask = handler.HandleConnectAsync(
            connection,
            connectRequest,
            ReadOnlySequence<byte>.Empty,
            cts.Token).AsTask();

        var establishedResponse = await ReadHeadersAsStringAsync(downstream.PeerSide, cts.Token);
        Assert.Contains("200 Connection established", establishedResponse, StringComparison.Ordinal);

        await downstream.PeerSide.SendAllAsync("ping"u8.ToArray(), cts.Token);
        downstream.PeerSide.Shutdown(SocketShutdown.Send);
        var echoed = await ReceiveExactlyAsync(downstream.PeerSide, 4, cts.Token);
        Assert.Equal("ping"u8.ToArray(), echoed);

        await socksTask;
        await handlerTask;
    }

    [Fact]
    public async Task HandleConnectAsync_DirectConnect_ClientHalfClose_StillForwardsServerData()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var downstream = await CreateConnectedSocketsAsync(cts.Token);
        using var targetListener = new TcpListener(IPAddress.Loopback, 0);
        targetListener.Start();

        var targetEndpoint = (IPEndPoint)targetListener.LocalEndpoint;
        var config = Configuration.Default with
        {
            Verbose = false,
            ConnectIdleTimeout = TimeSpan.FromSeconds(2)
        };

        var logger = new NullLogger();
        var stats = new Stats();
        using var accessLogger = new AccessLogger(config, logger);
        var loopDetector = new LoopDetector();
        using var connection = new Connection(downstream.ProxySide, logger, config, stats, accessLogger, loopDetector);
        var handler = new ConnectHandler(logger, config, stats, accessLogger, "127.0.0.1", loopDetector);

        var connectRequest = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Connect,
            Uri = $"127.0.0.1:{targetEndpoint.Port}",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes($"127.0.0.1:{targetEndpoint.Port}"))
            }
        };

        var targetTask = Task.Run(async () =>
        {
            using var targetSocket = await targetListener.AcceptSocketAsync(cts.Token);
            var payload = await ReceiveExactlyAsync(targetSocket, 4, cts.Token);
            Assert.Equal("ping"u8.ToArray(), payload);

            await Task.Delay(150, cts.Token);
            await targetSocket.SendAllAsync("pong"u8.ToArray(), cts.Token);
            targetSocket.Shutdown(SocketShutdown.Send);
        }, cts.Token);

        var handlerTask = handler.HandleConnectAsync(
            connection,
            connectRequest,
            ReadOnlySequence<byte>.Empty,
            cts.Token).AsTask();

        var establishedResponse = await ReadHeadersAsStringAsync(downstream.PeerSide, cts.Token);
        Assert.Contains("200 Connection established", establishedResponse, StringComparison.Ordinal);

        await downstream.PeerSide.SendAllAsync("ping"u8.ToArray(), cts.Token);
        downstream.PeerSide.Shutdown(SocketShutdown.Send);

        var echoed = await ReceiveExactlyAsync(downstream.PeerSide, 4, cts.Token);
        Assert.Equal("pong"u8.ToArray(), echoed);

        await targetTask;
        await handlerTask;
    }

    [Fact]
    public async Task HandleConnectAsync_DirectConnect_ActiveTraffic_DoesNotTriggerIdleTimeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var downstream = await CreateConnectedSocketsAsync(cts.Token);
        using var targetListener = new TcpListener(IPAddress.Loopback, 0);
        targetListener.Start();

        var targetEndpoint = (IPEndPoint)targetListener.LocalEndpoint;
        var config = Configuration.Default with
        {
            Verbose = false,
            ConnectIdleTimeout = TimeSpan.FromMilliseconds(500)
        };

        var logger = new NullLogger();
        var stats = new Stats();
        using var accessLogger = new AccessLogger(config, logger);
        var loopDetector = new LoopDetector();
        using var connection = new Connection(downstream.ProxySide, logger, config, stats, accessLogger, loopDetector);
        var handler = new ConnectHandler(logger, config, stats, accessLogger, "127.0.0.1", loopDetector);

        var connectRequest = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Connect,
            Uri = $"127.0.0.1:{targetEndpoint.Port}",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes($"127.0.0.1:{targetEndpoint.Port}"))
            }
        };

        var targetTask = Task.Run(async () =>
        {
            using var targetSocket = await targetListener.AcceptSocketAsync(cts.Token);

            for (var i = 0; i < 5; i++)
            {
                var incoming = await ReceiveExactlyAsync(targetSocket, 1, cts.Token);
                await targetSocket.SendAllAsync(incoming, cts.Token);
                await Task.Delay(150, cts.Token);
            }

            targetSocket.Shutdown(SocketShutdown.Send);
        }, cts.Token);

        var handlerTask = handler.HandleConnectAsync(
            connection,
            connectRequest,
            ReadOnlySequence<byte>.Empty,
            cts.Token).AsTask();

        var establishedResponse = await ReadHeadersAsStringAsync(downstream.PeerSide, cts.Token);
        Assert.Contains("200 Connection established", establishedResponse, StringComparison.Ordinal);

        for (byte i = 0; i < 5; i++)
        {
            await downstream.PeerSide.SendAllAsync(new byte[] { i }, cts.Token);
            var echoed = await ReceiveExactlyAsync(downstream.PeerSide, 1, cts.Token);
            Assert.Equal(i, echoed[0]);
            await Task.Delay(150, cts.Token);
        }

        downstream.PeerSide.Shutdown(SocketShutdown.Send);

        await targetTask;
        await handlerTask.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
        Assert.True(handlerTask.IsCompletedSuccessfully);
    }

    private static async Task<string> ReadHeadersAsStringAsync(Socket socket, CancellationToken token)
    {
        var buffer = new byte[8192];
        var received = 0;

        while (received < buffer.Length)
        {
            var read = await socket.ReceiveAsync(
                buffer.AsMemory(received),
                SocketFlags.None,
                token);
            if (read == 0) throw new EndOfStreamException("Socket closed before headers were complete.");
            received += read;

            if (TryFindHeadersEnd(buffer.AsSpan(0, received), out var headerEnd))
                return Encoding.ASCII.GetString(buffer, 0, headerEnd);
        }

        throw new InvalidOperationException("Header block exceeds test buffer.");
    }

    private static async Task<(string Headers, byte[] Remaining)> ReadHeadersAndRemainingAsync(Socket socket, CancellationToken token)
    {
        var buffer = new byte[8192];
        var received = 0;

        while (received < buffer.Length)
        {
            var read = await socket.ReceiveAsync(
                buffer.AsMemory(received),
                SocketFlags.None,
                token);
            if (read == 0) throw new EndOfStreamException("Socket closed before headers were complete.");
            received += read;

            if (TryFindHeadersEnd(buffer.AsSpan(0, received), out var headerEnd))
            {
                var headers = Encoding.ASCII.GetString(buffer, 0, headerEnd);
                var remainingLength = received - headerEnd;
                if (remainingLength <= 0) return (headers, []);

                var remaining = new byte[remainingLength];
                Buffer.BlockCopy(buffer, headerEnd, remaining, 0, remainingLength);
                return (headers, remaining);
            }
        }

        throw new InvalidOperationException("Header block exceeds test buffer.");
    }

    private static bool TryFindHeadersEnd(ReadOnlySpan<byte> bytes, out int headerEnd)
    {
        headerEnd = -1;

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
            if (read == 0) throw new EndOfStreamException("Socket closed before expected bytes were received.");
            offset += read;
        }

        return buffer;
    }

    private static async Task<byte[]> ReceiveBodyWithPrefetchedAsync(
        Socket socket,
        byte[] prefetched,
        int length,
        CancellationToken token)
    {
        if (length <= 0) return [];

        if (prefetched.Length >= length)
        {
            var exact = new byte[length];
            Buffer.BlockCopy(prefetched, 0, exact, 0, length);
            return exact;
        }

        var buffer = new byte[length];
        var offset = 0;

        if (prefetched.Length > 0)
        {
            Buffer.BlockCopy(prefetched, 0, buffer, 0, prefetched.Length);
            offset = prefetched.Length;
        }

        while (offset < length)
        {
            var read = await socket.ReceiveAsync(
                buffer.AsMemory(offset),
                SocketFlags.None,
                token);
            if (read == 0) throw new EndOfStreamException("Socket closed before expected bytes were received.");
            offset += read;
        }

        return buffer;
    }

    private static int ParseContentLength(string headers)
    {
        foreach (var line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line.Substring("Content-Length:".Length).Trim();
            return int.TryParse(value, out var parsed) ? parsed : 0;
        }

        return 0;
    }

    private static IPAddress GetAvailableLoopbackAddress()
    {
        var candidate = IPAddress.Parse("127.0.0.2");
        if (CanBindAddress(candidate)) return candidate;
        return IPAddress.Loopback;
    }

    private static bool CanBindAddress(IPAddress address)
    {
        var listener = new TcpListener(address, 0);
        try
        {
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<SocketPair> CreateConnectedSocketsAsync(CancellationToken token, IPAddress? listenAddress = null)
    {
        var listener = new TcpListener(listenAddress ?? IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var client = new Socket(SocketType.Stream, ProtocolType.Tcp);
            var acceptTask = listener.AcceptSocketAsync(token);

            await client.ConnectAsync(endpoint, token);
            var proxy = await acceptTask;

            return new SocketPair(proxy, client);
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
