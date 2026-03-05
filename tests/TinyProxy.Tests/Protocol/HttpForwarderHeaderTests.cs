namespace TinyProxy.Tests.Protocol;

public class HttpForwarderHeaderTests
{
    private static readonly MethodInfo s_buildForwardRequestMethod =
        typeof(HttpForwarder).GetMethod("BuildForwardRequest", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo s_connectViaUpstreamMethod =
        typeof(HttpForwarder).GetMethod("ConnectViaUpstreamAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo s_forwardRequestBodyMethod =
        typeof(HttpForwarder).GetMethod(
            "ForwardRequestBodyAsync",
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new[] { typeof(Socket), typeof(Socket), typeof(HttpRequest), typeof(CancellationToken), typeof(Action) },
            null)!;

    [Fact]
    public void BuildForwardRequest_UsesClientIpInXTinyproxyHeader_WhenEnabled()
    {
        var config = Configuration.Default with
        {
            AddXTinyproxyHeader = true,
            AddViaHeader = false,
            Verbose = false
        };

        var forwarder = new HttpForwarder(
            new NullLogger(),
            config,
            new Stats(),
            new AccessLogger(config, new NullLogger()),
            "192.0.2.10");

        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Get,
            Uri = "http://example.com/",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("example.com"u8.ToArray())
            },
            Body = ReadOnlySequence<byte>.Empty
        };

        var bytes = (byte[])s_buildForwardRequestMethod.Invoke(
            forwarder,
            new object[] { request, "example.com", 80, false })!;

        var payload = Encoding.ASCII.GetString(bytes);
        Assert.Contains("X-Tinyproxy: 192.0.2.10\r\n", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildForwardRequest_DoesNotInjectXForwardedOrProxyConnectionByDefault()
    {
        var config = Configuration.Default with
        {
            AddViaHeader = false,
            Verbose = false,
            UpstreamProxy = new UpstreamProxyConfig
            {
                Host = "upstream.example",
                Port = 3128,
                Type = UpstreamProxyType.Http
            }
        };

        var forwarder = new HttpForwarder(
            new NullLogger(),
            config,
            new Stats(),
            new AccessLogger(config, new NullLogger()),
            "192.0.2.10");

        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Get,
            Uri = "http://example.com/",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("example.com"u8.ToArray())
            },
            Body = ReadOnlySequence<byte>.Empty
        };

        var bytes = (byte[])s_buildForwardRequestMethod.Invoke(
            forwarder,
            new object[] { request, "example.com", 80, true })!;

        var payload = Encoding.ASCII.GetString(bytes);
        Assert.DoesNotContain("X-Forwarded-For:", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Forwarded-Host:", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Forwarded-Proto:", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Proxy-Connection:", payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Connection: close\r\n", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildForwardRequest_RemovesHeadersListedInConnectionOptions()
    {
        var config = Configuration.Default with
        {
            AddViaHeader = false,
            Verbose = false
        };

        var forwarder = new HttpForwarder(
            new NullLogger(),
            config,
            new Stats(),
            new AccessLogger(config, new NullLogger()),
            "192.0.2.10");

        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Get,
            Uri = "http://example.com/",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("example.com"u8.ToArray()),
                ["Connection"] = new ReadOnlySequence<byte>("keep-alive, X-Hop-By-Hop, weird-token"u8.ToArray()),
                ["Proxy-Connection"] = new ReadOnlySequence<byte>("Proxy-Only"u8.ToArray()),
                ["X-Hop-By-Hop"] = new ReadOnlySequence<byte>("secret"u8.ToArray()),
                ["Weird-Token"] = new ReadOnlySequence<byte>("secret2"u8.ToArray()),
                ["Proxy-Only"] = new ReadOnlySequence<byte>("secret3"u8.ToArray()),
                ["User-Agent"] = new ReadOnlySequence<byte>("tinyproxy-test"u8.ToArray())
            },
            Body = ReadOnlySequence<byte>.Empty
        };

        var bytes = (byte[])s_buildForwardRequestMethod.Invoke(
            forwarder,
            new object[] { request, "example.com", 80, false })!;

        var payload = Encoding.ASCII.GetString(bytes);
        Assert.DoesNotContain("X-Hop-By-Hop:", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Weird-Token:", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Proxy-Only:", payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("User-Agent: tinyproxy-test\r\n", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildForwardRequest_RemovesHeadersListedInDuplicateConnectionLines()
    {
        var config = Configuration.Default with
        {
            AddViaHeader = false,
            Verbose = false
        };

        var forwarder = new HttpForwarder(
            new NullLogger(),
            config,
            new Stats(),
            new AccessLogger(config, new NullLogger()),
            "192.0.2.10");

        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Get,
            Uri = "http://example.com/",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("example.com"u8.ToArray()),
                ["Connection"] = new ReadOnlySequence<byte>("keep-alive"u8.ToArray())
            },
            HeaderLines = new[]
            {
                new KeyValuePair<string, ReadOnlySequence<byte>>("Host", new ReadOnlySequence<byte>("example.com"u8.ToArray())),
                new KeyValuePair<string, ReadOnlySequence<byte>>("Connection", new ReadOnlySequence<byte>("keep-alive"u8.ToArray())),
                new KeyValuePair<string, ReadOnlySequence<byte>>("Connection", new ReadOnlySequence<byte>("X-Hop-By-Hop-2"u8.ToArray())),
                new KeyValuePair<string, ReadOnlySequence<byte>>("X-Hop-By-Hop-2", new ReadOnlySequence<byte>("secret"u8.ToArray())),
                new KeyValuePair<string, ReadOnlySequence<byte>>("User-Agent", new ReadOnlySequence<byte>("tinyproxy-test"u8.ToArray()))
            },
            Body = ReadOnlySequence<byte>.Empty
        };

        var bytes = (byte[])s_buildForwardRequestMethod.Invoke(
            forwarder,
            new object[] { request, "example.com", 80, false })!;

        var payload = Encoding.ASCII.GetString(bytes);
        Assert.DoesNotContain("X-Hop-By-Hop-2:", payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("User-Agent: tinyproxy-test\r\n", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildForwardRequest_DowngradesNonHttp1VersionToHttp10()
    {
        var config = Configuration.Default with
        {
            AddViaHeader = false,
            Verbose = false
        };

        var forwarder = new HttpForwarder(
            new NullLogger(),
            config,
            new Stats(),
            new AccessLogger(config, new NullLogger()),
            "192.0.2.10");

        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Get,
            Uri = "http://example.com/",
            Version = "HTTP/0.9",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("example.com"u8.ToArray())
            },
            Body = ReadOnlySequence<byte>.Empty
        };

        var bytes = (byte[])s_buildForwardRequestMethod.Invoke(
            forwarder,
            new object[] { request, "example.com", 80, false })!;

        var payload = Encoding.ASCII.GetString(bytes);
        Assert.StartsWith("GET / HTTP/1.0\r\n", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildForwardRequest_UsesRequestVersionInViaHeader()
    {
        var config = Configuration.Default with
        {
            AddViaHeader = true,
            ViaProxyName = "proxy-edge",
            Verbose = false
        };

        var forwarder = new HttpForwarder(
            new NullLogger(),
            config,
            new Stats(),
            new AccessLogger(config, new NullLogger()),
            "192.0.2.10");

        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Get,
            Uri = "http://example.com/",
            Version = "HTTP/1.0",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("example.com"u8.ToArray())
            },
            Body = ReadOnlySequence<byte>.Empty
        };

        var bytes = (byte[])s_buildForwardRequestMethod.Invoke(
            forwarder,
            new object[] { request, "example.com", 80, false })!;

        var payload = Encoding.ASCII.GetString(bytes);
        Assert.Contains("Via: 1.0 proxy-edge\r\n", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildForwardRequest_NormalizesTrailingHttpVersionToken_LikeTinyproxySscanf()
    {
        var config = Configuration.Default with
        {
            AddViaHeader = true,
            ViaProxyName = "proxy-edge",
            Verbose = false
        };

        var forwarder = new HttpForwarder(
            new NullLogger(),
            config,
            new Stats(),
            new AccessLogger(config, new NullLogger()),
            "192.0.2.10");

        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Get,
            Uri = "http://example.com/",
            Version = "HTTP/1.1beta",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("example.com"u8.ToArray())
            },
            Body = ReadOnlySequence<byte>.Empty
        };

        var bytes = (byte[])s_buildForwardRequestMethod.Invoke(
            forwarder,
            new object[] { request, "example.com", 80, false })!;

        var payload = Encoding.ASCII.GetString(bytes);
        Assert.StartsWith("GET / HTTP/1.1\r\n", payload, StringComparison.Ordinal);
        Assert.Contains("Via: 1.1 proxy-edge\r\n", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildForwardRequest_PreservesUnknownMethodToken()
    {
        var config = Configuration.Default with
        {
            AddViaHeader = false,
            Verbose = false
        };

        var forwarder = new HttpForwarder(
            new NullLogger(),
            config,
            new Stats(),
            new AccessLogger(config, new NullLogger()),
            "192.0.2.10");

        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.None,
            RawMethod = "PROPFIND",
            Uri = "http://example.com/",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("example.com"u8.ToArray())
            },
            Body = ReadOnlySequence<byte>.Empty
        };

        var bytes = (byte[])s_buildForwardRequestMethod.Invoke(
            forwarder,
            new object[] { request, "example.com", 80, false })!;

        var payload = Encoding.ASCII.GetString(bytes);
        Assert.StartsWith("PROPFIND / HTTP/1.1\r\n", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildForwardRequest_PreservesDuplicateHeaders_FromHeaderLines()
    {
        var config = Configuration.Default with
        {
            AddViaHeader = false,
            Verbose = false
        };

        var forwarder = new HttpForwarder(
            new NullLogger(),
            config,
            new Stats(),
            new AccessLogger(config, new NullLogger()),
            "192.0.2.10");

        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Get,
            Uri = "http://example.com/",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("example.com"u8.ToArray()),
                ["X-Test"] = new ReadOnlySequence<byte>("first"u8.ToArray())
            },
            HeaderLines = new[]
            {
                new KeyValuePair<string, ReadOnlySequence<byte>>("Host", new ReadOnlySequence<byte>("example.com"u8.ToArray())),
                new KeyValuePair<string, ReadOnlySequence<byte>>("X-Test", new ReadOnlySequence<byte>("first"u8.ToArray())),
                new KeyValuePair<string, ReadOnlySequence<byte>>("X-Test", new ReadOnlySequence<byte>("second"u8.ToArray()))
            },
            Body = ReadOnlySequence<byte>.Empty
        };

        var bytes = (byte[])s_buildForwardRequestMethod.Invoke(
            forwarder,
            new object[] { request, "example.com", 80, false })!;

        var payload = Encoding.ASCII.GetString(bytes);
        Assert.Contains("X-Test: first\r\n", payload, StringComparison.Ordinal);
        Assert.Contains("X-Test: second\r\n", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildForwardRequest_WithHttpUpstreamCredentials_DropsClientAndAddsUpstreamProxyAuthorization()
    {
        var config = Configuration.Default with
        {
            AddViaHeader = false,
            Verbose = false,
            UpstreamProxy = new UpstreamProxyConfig
            {
                Host = "upstream.example",
                Port = 3128,
                Type = UpstreamProxyType.Http,
                Username = "alice",
                Password = "secret"
            }
        };

        var forwarder = new HttpForwarder(
            new NullLogger(),
            config,
            new Stats(),
            new AccessLogger(config, new NullLogger()),
            "192.0.2.10");

        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Get,
            Uri = "http://example.com/",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("example.com"u8.ToArray()),
                ["Proxy-Authorization"] = new ReadOnlySequence<byte>("Basic Y2xpZW50OmNyZWRz"u8.ToArray())
            },
            Body = ReadOnlySequence<byte>.Empty
        };

        var bytes = (byte[])s_buildForwardRequestMethod.Invoke(
            forwarder,
            new object[] { request, "example.com", 80, true })!;

        var payload = Encoding.ASCII.GetString(bytes);
        Assert.Contains("Proxy-Authorization: Basic YWxpY2U6c2VjcmV0\r\n", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Proxy-Authorization: Basic Y2xpZW50OmNyZWRz\r\n", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildForwardRequest_DropsClientProxyAuthorization_WhenLocalBasicAuthDisabled()
    {
        var config = Configuration.Default with
        {
            AddViaHeader = false,
            Verbose = false
        };

        var forwarder = new HttpForwarder(
            new NullLogger(),
            config,
            new Stats(),
            new AccessLogger(config, new NullLogger()),
            "192.0.2.10");

        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Get,
            Uri = "http://example.com/",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("example.com"u8.ToArray()),
                ["Proxy-Authorization"] = new ReadOnlySequence<byte>("Basic Y2xpZW50OmNyZWRz"u8.ToArray())
            },
            Body = ReadOnlySequence<byte>.Empty
        };

        var bytes = (byte[])s_buildForwardRequestMethod.Invoke(
            forwarder,
            new object[] { request, "example.com", 80, false })!;

        var payload = Encoding.ASCII.GetString(bytes);
        Assert.DoesNotContain("Proxy-Authorization: Basic Y2xpZW50OmNyZWRz\r\n", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildForwardRequest_FiltersCustomHeaders_WhenAnonymousModeEnabled()
    {
        var config = Configuration.Default with
        {
            AddViaHeader = false,
            Verbose = false,
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

        var forwarder = new HttpForwarder(
            new NullLogger(),
            config,
            new Stats(),
            new AccessLogger(config, new NullLogger()),
            "192.0.2.10");

        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Get,
            Uri = "http://example.com/",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("example.com"u8.ToArray())
            },
            Body = ReadOnlySequence<byte>.Empty
        };

        var bytes = (byte[])s_buildForwardRequestMethod.Invoke(
            forwarder,
            new object[] { request, "example.com", 80, false })!;

        var payload = Encoding.ASCII.GetString(bytes);
        Assert.Contains("X-Custom-Allow: allowed\r\n", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Custom-Drop: blocked\r\n", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildForwardRequest_IgnoresConflictingCustomHopByHopHeaders()
    {
        var config = Configuration.Default with
        {
            AddViaHeader = false,
            Verbose = false,
            CustomHeaders = new List<HttpHeader>
            {
                new() { Name = "Host", Value = "evil.example" },
                new() { Name = "Connection", Value = "keep-alive" },
                new() { Name = "Proxy-Authorization", Value = "Basic ZXZpbDpldmls" },
                new() { Name = "X-Custom-Allow", Value = "allowed" }
            }
        };

        var forwarder = new HttpForwarder(
            new NullLogger(),
            config,
            new Stats(),
            new AccessLogger(config, new NullLogger()),
            "192.0.2.10");

        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Get,
            Uri = "http://example.com/",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("example.com"u8.ToArray())
            },
            Body = ReadOnlySequence<byte>.Empty
        };

        var bytes = (byte[])s_buildForwardRequestMethod.Invoke(
            forwarder,
            new object[] { request, "example.com", 80, false })!;

        var payload = Encoding.ASCII.GetString(bytes);
        Assert.Contains("Host: example.com\r\n", payload, StringComparison.Ordinal);
        Assert.Contains("Connection: close\r\n", payload, StringComparison.Ordinal);
        Assert.Contains("X-Custom-Allow: allowed\r\n", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Host: evil.example\r\n", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Connection: keep-alive\r\n", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Proxy-Authorization: Basic ZXZpbDpldmls\r\n", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildForwardRequest_AnonymousMode_PreservesImplicitContentHeadersLikeTinyproxyUpstream()
    {
        var config = Configuration.Default with
        {
            AddViaHeader = false,
            Verbose = false,
            AnonymousAllowedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "User-Agent"
            }
        };

        var forwarder = new HttpForwarder(
            new NullLogger(),
            config,
            new Stats(),
            new AccessLogger(config, new NullLogger()),
            "192.0.2.10");

        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Post,
            Uri = "http://example.com/upload",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("example.com"u8.ToArray()),
                ["User-Agent"] = new ReadOnlySequence<byte>("tinyproxy-test"u8.ToArray()),
                ["Content-Length"] = new ReadOnlySequence<byte>("4"u8.ToArray()),
                ["Content-Type"] = new ReadOnlySequence<byte>("text/plain"u8.ToArray())
            },
            Body = new ReadOnlySequence<byte>("data"u8.ToArray())
        };

        var bytes = (byte[])s_buildForwardRequestMethod.Invoke(
            forwarder,
            new object[] { request, "example.com", 80, false })!;

        var payload = Encoding.ASCII.GetString(bytes);
        Assert.Contains("User-Agent: tinyproxy-test\r\n", payload, StringComparison.Ordinal);
        Assert.Contains("Content-Length: 4\r\n", payload, StringComparison.Ordinal);
        Assert.Contains("Content-Type: text/plain\r\n", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectViaUpstreamAsync_AppliesBindAddresses_ForHttpUpstream()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var downstream = await CreateConnectedSocketsAsync(cts.Token);
        using var upstreamListener = new TcpListener(IPAddress.Loopback, 0);
        upstreamListener.Start();

        var upstreamEndpoint = (IPEndPoint)upstreamListener.LocalEndpoint;
        var bindAddress = GetAvailableLoopbackAddress().ToString();
        var config = Configuration.Default with
        {
            Verbose = false,
            BindAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                bindAddress
            }
        };

        var forwarder = new HttpForwarder(
            new NullLogger(),
            config,
            new Stats(),
            new AccessLogger(config, new NullLogger()),
            "192.0.2.10");

        var upstream = new UpstreamProxyConfig
        {
            Host = upstreamEndpoint.Address.ToString(),
            Port = (ushort)upstreamEndpoint.Port,
            Type = UpstreamProxyType.Http
        };

        var connectTask = (Task<Socket>)s_connectViaUpstreamMethod.Invoke(
            forwarder,
            new object?[] { upstream, "example.com", 80, downstream.ProxySide, cts.Token })!;
        var acceptTask = upstreamListener.AcceptSocketAsync(cts.Token);

        using var upstreamSocket = await connectTask;
        using var accepted = await acceptTask;

        var remote = Assert.IsType<IPEndPoint>(accepted.RemoteEndPoint);
        Assert.Equal(IPAddress.Parse(bindAddress), remote.Address);
    }

    [Fact]
    public async Task ConnectViaUpstreamAsync_AppliesBindAddresses_ForSocksUpstream()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var downstream = await CreateConnectedSocketsAsync(cts.Token);
        using var socksListener = new TcpListener(IPAddress.Loopback, 0);
        socksListener.Start();

        var socksEndpoint = (IPEndPoint)socksListener.LocalEndpoint;
        var bindAddress = GetAvailableLoopbackAddress().ToString();
        var config = Configuration.Default with
        {
            Verbose = false,
            BindAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                bindAddress
            }
        };

        var forwarder = new HttpForwarder(
            new NullLogger(),
            config,
            new Stats(),
            new AccessLogger(config, new NullLogger()),
            "192.0.2.10");

        var upstream = new UpstreamProxyConfig
        {
            Host = socksEndpoint.Address.ToString(),
            Port = (ushort)socksEndpoint.Port,
            Type = UpstreamProxyType.Socks5
        };

        var serverTask = Task.Run(async () =>
        {
            using var socksSocket = await socksListener.AcceptSocketAsync(cts.Token);
            var remoteEndPoint = Assert.IsType<IPEndPoint>(socksSocket.RemoteEndPoint);
            Assert.Equal(IPAddress.Parse(bindAddress), remoteEndPoint.Address);

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
            Assert.Equal("example.com", host);
            Assert.Equal(80, port);

            await socksSocket.SendAllAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, cts.Token);
        }, cts.Token);

        var connectTask = (Task<Socket>)s_connectViaUpstreamMethod.Invoke(
            forwarder,
            new object?[] { upstream, "example.com", 80, downstream.ProxySide, cts.Token })!;

        using var upstreamSocket = await connectTask;
        Assert.True(upstreamSocket.Connected);
        await serverTask;
    }

    [Fact]
    public void BuildForwardRequest_DoesNotAddProxyAuthorization_ForSocksUpstream()
    {
        var config = Configuration.Default with
        {
            AddViaHeader = false,
            Verbose = false,
            UpstreamProxy = new UpstreamProxyConfig
            {
                Host = "socks.example",
                Port = 1080,
                Type = UpstreamProxyType.Socks5,
                Username = "alice",
                Password = "secret"
            }
        };

        var forwarder = new HttpForwarder(
            new NullLogger(),
            config,
            new Stats(),
            new AccessLogger(config, new NullLogger()),
            "192.0.2.10");

        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Get,
            Uri = "http://example.com/",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("example.com"u8.ToArray())
            },
            Body = ReadOnlySequence<byte>.Empty
        };

        var bytes = (byte[])s_buildForwardRequestMethod.Invoke(
            forwarder,
            new object[] { request, "example.com", 80, false })!;

        var payload = Encoding.ASCII.GetString(bytes);
        Assert.DoesNotContain("Proxy-Authorization:", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForwardRequestBodyAsync_PrefersContentLength_WhenTransferEncodingChunkedAlsoPresent()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var downstream = await CreateConnectedSocketsAsync(cts.Token);
        using var upstream = await CreateConnectedSocketsAsync(cts.Token);

        var config = Configuration.Default with
        {
            AddViaHeader = false,
            Verbose = false
        };

        var forwarder = new HttpForwarder(
            new NullLogger(),
            config,
            new Stats(),
            new AccessLogger(config, new NullLogger()),
            "192.0.2.10");

        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Post,
            Uri = "http://example.com/upload",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("example.com"u8.ToArray()),
                ["Transfer-Encoding"] = new ReadOnlySequence<byte>("chunked"u8.ToArray())
            },
            ContentLength = 4,
            Body = new ReadOnlySequence<byte>("12"u8.ToArray())
        };

        var forwardTask = InvokeForwardRequestBodyAsync(
            forwarder,
            downstream.ProxySide,
            upstream.ProxySide,
            request,
            cts.Token);

        await downstream.PeerSide.SendAllAsync("34"u8.ToArray(), cts.Token);
        downstream.PeerSide.Shutdown(SocketShutdown.Send);

        var forwardedBody = await ReceiveExactlyAsync(upstream.PeerSide, 4, cts.Token);
        await forwardTask;

        Assert.Equal("1234", Encoding.ASCII.GetString(forwardedBody));
    }

    private static async Task InvokeForwardRequestBodyAsync(
        HttpForwarder forwarder,
        Socket clientSocket,
        Socket serverSocket,
        HttpRequest request,
        CancellationToken token)
    {
        var forwardTask = (ValueTask)s_forwardRequestBodyMethod.Invoke(
            forwarder,
            new object?[] { clientSocket, serverSocket, request, token, null })!;

        await forwardTask;
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

    private static async Task<SocketPair> CreateConnectedSocketsAsync(CancellationToken token)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
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
