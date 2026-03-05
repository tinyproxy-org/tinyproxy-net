namespace TinyProxy.Tests.Core;

public class ConnectionAuthRoutingTests
{
    [Fact]
    public async Task ProcessAsync_InvalidRequestLine_ReturnsBadRequest()
    {
        var response = await SendRequestAsync(
            Configuration.Default with { Verbose = false },
            "INVALID_REQUEST_LINE\r\n\r\n");

        Assert.Contains("400 Bad Request", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_DeniedIp_ReturnsForbidden_BeforeRequestParsing()
    {
        var response = await SendRequestAsync(
            Configuration.Default with
            {
                Verbose = false,
                DenyIPs = new HashSet<string> { "127.0.0.1" }
            },
            "INVALID_REQUEST_LINE\r\n\r\n");

        Assert.Contains("403 Forbidden", response, StringComparison.Ordinal);
        Assert.DoesNotContain("400 Bad Request", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_UnsupportedAbsoluteScheme_ReturnsNotImplemented()
    {
        var response = await SendRequestAsync(
            Configuration.Default with { Verbose = false },
            "GET ftp://example.com/file HTTP/1.1\r\nHost: example.com\r\n\r\n");

        Assert.Contains("501 Not Implemented", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_HttpsAbsoluteUri_ReturnsNotImplementedLikeTinyproxyUpstream()
    {
        var response = await SendRequestAsync(
            Configuration.Default with { Verbose = false },
            "GET https://example.com/secure HTTP/1.1\r\nHost: example.com\r\n\r\n");

        Assert.Contains("501 Not Implemented", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_ReverseOnly_UnmappedRequest_ReturnsBadRequest()
    {
        var config = Configuration.Default with
        {
            Verbose = false,
            IsReverseProxyEnabled = true,
            ReverseOnly = true,
            ReversePaths = new List<ReversePathConfig>
            {
                new() { Path = "/mapped/", Url = "http://backend.example" }
            }
        };

        var response = await SendRequestAsync(
            config,
            "GET ftp://example.com/file HTTP/1.1\r\nHost: example.com\r\n\r\n");

        Assert.Contains("400 Bad Request", response, StringComparison.Ordinal);
        Assert.DoesNotContain("501 Not Implemented", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_ReverseProxy_PathWithoutTrailingSlash_ReturnsMovedPermanently()
    {
        var config = Configuration.Default with
        {
            Verbose = false,
            IsReverseProxyEnabled = true,
            ReversePaths = new List<ReversePathConfig>
            {
                new() { Path = "/app/", Url = "http://backend.example/" }
            }
        };

        var response = await SendRequestAsync(
            config,
            "GET /app HTTP/1.1\r\nHost: proxy.local\r\n\r\n");

        Assert.Contains("301 Moved Permanently", response, StringComparison.Ordinal);
        Assert.Contains("Location: /app/", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_ReverseProxy_RewriteKeepsConfiguredTrailingSlashLikeTinyproxyUpstream()
    {
        using var backendListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        backendListener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        backendListener.Listen(1);
        var backendPort = ((IPEndPoint)backendListener.LocalEndPoint!).Port;

        var backendRequestLineTask = AcceptBackendRequestLineAndRespondAsync(backendListener);

        var config = Configuration.Default with
        {
            Verbose = false,
            IsReverseProxyEnabled = true,
            ReversePaths = new List<ReversePathConfig>
            {
                new() { Path = "/app/", Url = $"http://127.0.0.1:{backendPort}/base/" }
            }
        };

        var response = await SendRequestAsync(
            config,
            "GET /app/test?x=1 HTTP/1.1\r\nHost: proxy.local\r\nConnection: close\r\n\r\n");

        var backendRequestLine = await backendRequestLineTask;

        Assert.Contains("200 OK", response, StringComparison.Ordinal);
        Assert.Equal("GET /base/test?x=1 HTTP/1.1", backendRequestLine);
    }

    [Fact]
    public async Task ProcessAsync_ReverseProxy_MissingTrailingSlashWithQuery_RedirectsAndPreservesQuery()
    {
        var config = Configuration.Default with
        {
            Verbose = false,
            IsReverseProxyEnabled = true,
            ReversePaths = new List<ReversePathConfig>
            {
                new() { Path = "/app/", Url = "http://127.0.0.1:65535/base/" }
            }
        };

        var response = await SendRequestAsync(
            config,
            "GET /app?x=1 HTTP/1.1\r\nHost: proxy.local\r\nConnection: close\r\n\r\n");

        Assert.Contains("301 Moved Permanently", response, StringComparison.Ordinal);
        Assert.Contains("Location: /app/?x=1", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_ReverseProxy_MagicCookie_AddsTrackingSetCookieHeader()
    {
        using var backendListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        backendListener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        backendListener.Listen(1);
        var backendPort = ((IPEndPoint)backendListener.LocalEndPoint!).Port;

        var backendRequestLineTask = AcceptBackendRequestLineAndRespondAsync(backendListener);

        var config = Configuration.Default with
        {
            Verbose = false,
            IsReverseProxyEnabled = true,
            ReverseMagicEnabled = true,
            ReversePaths = new List<ReversePathConfig>
            {
                new() { Path = "/app/", Url = $"http://127.0.0.1:{backendPort}/base/" }
            }
        };

        var response = await SendRequestAsync(
            config,
            "GET /app/test HTTP/1.1\r\nHost: proxy.local\r\nConnection: close\r\n\r\n");

        _ = await backendRequestLineTask;

        Assert.Contains("200 OK", response, StringComparison.Ordinal);
        Assert.Contains("Set-Cookie: yummy_magical_cookie=/app/; path=/", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_ReverseProxy_MagicCookie_RoutesUnmappedPathUsingCookieRule()
    {
        using var backendListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        backendListener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        backendListener.Listen(1);
        var backendPort = ((IPEndPoint)backendListener.LocalEndPoint!).Port;

        var backendRequestLineTask = AcceptBackendRequestLineAndRespondAsync(backendListener);

        var config = Configuration.Default with
        {
            Verbose = false,
            IsReverseProxyEnabled = true,
            ReverseMagicEnabled = true,
            ReversePaths = new List<ReversePathConfig>
            {
                new() { Path = "/app/", Url = $"http://127.0.0.1:{backendPort}/base/" }
            }
        };

        var response = await SendRequestAsync(
            config,
            "GET /docs/page HTTP/1.1\r\nHost: proxy.local\r\nCookie: session=abc; yummy_magical_cookie=/app/; theme=light\r\nConnection: close\r\n\r\n");

        var backendRequestLine = await backendRequestLineTask;

        Assert.Contains("200 OK", response, StringComparison.Ordinal);
        Assert.Equal("GET /base/docs/page HTTP/1.1", backendRequestLine);
    }

    [Fact]
    public async Task ProcessAsync_ReverseProxy_MagicCookie_RoutesWithQuotedCookieValue()
    {
        using var backendListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        backendListener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        backendListener.Listen(1);
        var backendPort = ((IPEndPoint)backendListener.LocalEndPoint!).Port;

        var backendRequestLineTask = AcceptBackendRequestLineAndRespondAsync(backendListener);

        var config = Configuration.Default with
        {
            Verbose = false,
            IsReverseProxyEnabled = true,
            ReverseMagicEnabled = true,
            ReversePaths = new List<ReversePathConfig>
            {
                new() { Path = "/app/", Url = $"http://127.0.0.1:{backendPort}/base/" }
            }
        };

        var response = await SendRequestAsync(
            config,
            "GET /docs/page HTTP/1.1\r\nHost: proxy.local\r\nCookie: session=abc; yummy_magical_cookie=\"/app/\"; theme=light\r\nConnection: close\r\n\r\n");

        var backendRequestLine = await backendRequestLineTask;

        Assert.Contains("200 OK", response, StringComparison.Ordinal);
        Assert.Equal("GET /base/docs/page HTTP/1.1", backendRequestLine);
    }

    [Fact]
    public async Task ProcessAsync_ReverseProxy_MagicCookie_RoutesUsingLaterDuplicateCookieValueInSameHeader()
    {
        using var backendListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        backendListener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        backendListener.Listen(1);
        var backendPort = ((IPEndPoint)backendListener.LocalEndPoint!).Port;

        var backendRequestLineTask = AcceptBackendRequestLineAndRespondAsync(backendListener);

        var config = Configuration.Default with
        {
            Verbose = false,
            IsReverseProxyEnabled = true,
            ReverseMagicEnabled = true,
            ReversePaths = new List<ReversePathConfig>
            {
                new() { Path = "/app/", Url = $"http://127.0.0.1:{backendPort}/base/" }
            }
        };

        var response = await SendRequestAsync(
            config,
            "GET /docs/page HTTP/1.1\r\nHost: proxy.local\r\nCookie: yummy_magical_cookie=; session=abc; yummy_magical_cookie=/app/\r\nConnection: close\r\n\r\n");

        var completedTask = await Task.WhenAny(backendRequestLineTask, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(backendRequestLineTask, completedTask);
        var backendRequestLine = await backendRequestLineTask;

        Assert.Contains("200 OK", response, StringComparison.Ordinal);
        Assert.Equal("GET /base/docs/page HTTP/1.1", backendRequestLine);
    }

    [Fact]
    public async Task ProcessAsync_ReverseProxy_MagicCookie_RoutesWhenCookieAppearsInSecondCookieHeader()
    {
        using var backendListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        backendListener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        backendListener.Listen(1);
        var backendPort = ((IPEndPoint)backendListener.LocalEndPoint!).Port;

        var backendRequestLineTask = AcceptBackendRequestLineAndRespondAsync(backendListener);

        var config = Configuration.Default with
        {
            Verbose = false,
            IsReverseProxyEnabled = true,
            ReverseMagicEnabled = true,
            ReversePaths = new List<ReversePathConfig>
            {
                new() { Path = "/app/", Url = $"http://127.0.0.1:{backendPort}/base/" }
            }
        };

        var response = await SendRequestAsync(
            config,
            "GET /docs/page HTTP/1.1\r\nHost: proxy.invalid\r\nCookie: session=abc\r\nCookie: yummy_magical_cookie=/app/\r\nConnection: close\r\n\r\n");

        var completedTask = await Task.WhenAny(backendRequestLineTask, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(backendRequestLineTask, completedTask);
        var backendRequestLine = await backendRequestLineTask;

        Assert.Contains("200 OK", response, StringComparison.Ordinal);
        Assert.Equal("GET /base/docs/page HTTP/1.1", backendRequestLine);
    }

    [Fact]
    public async Task ProcessAsync_ReverseProxy_MagicCookie_DoesNotMatchPartialCookieName()
    {
        using var backendListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        backendListener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        backendListener.Listen(1);
        var backendPort = ((IPEndPoint)backendListener.LocalEndPoint!).Port;

        var backendRequestLineTask = AcceptBackendRequestLineAndRespondAsync(backendListener);

        var config = Configuration.Default with
        {
            Verbose = false,
            IsReverseProxyEnabled = true,
            ReverseMagicEnabled = true,
            ReversePaths = new List<ReversePathConfig>
            {
                new() { Path = "/app/", Url = $"http://127.0.0.1:{backendPort}/base/" }
            }
        };

        var response = await SendRequestAsync(
            config,
            "GET /docs/page HTTP/1.1\r\nHost: proxy.invalid\r\nCookie: session=abc; not_yummy_magical_cookie=/app/; theme=light\r\nConnection: close\r\n\r\n");

        Assert.Contains("500 Internal Server Error", response, StringComparison.Ordinal);
        Assert.DoesNotContain("200 OK", response, StringComparison.Ordinal);

        var completedTask = await Task.WhenAny(backendRequestLineTask, Task.Delay(250));
        Assert.NotSame(backendRequestLineTask, completedTask);

        backendListener.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(async () => await backendRequestLineTask);
    }

    [Fact]
    public async Task ProcessAsync_ReverseProxy_MatchBypassesStatHostPage()
    {
        using var backendListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        backendListener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        backendListener.Listen(1);
        var backendPort = ((IPEndPoint)backendListener.LocalEndPoint!).Port;

        var backendRequestLineTask = AcceptBackendRequestLineAndRespondAsync(backendListener);

        var config = Configuration.Default with
        {
            Verbose = false,
            StatHost = "stats.local",
            IsReverseProxyEnabled = true,
            ReversePaths = new List<ReversePathConfig>
            {
                new() { Path = "/app/", Url = $"http://127.0.0.1:{backendPort}/base/" }
            }
        };

        var response = await SendRequestAsync(
            config,
            "GET /app/test HTTP/1.1\r\nHost: stats.local\r\nConnection: close\r\n\r\n");

        var backendRequestLine = await backendRequestLineTask;

        Assert.Contains("200 OK", response, StringComparison.Ordinal);
        Assert.Equal("GET /base/test HTTP/1.1", backendRequestLine);
    }

    [Fact]
    public async Task ProcessAsync_TransparentProxy_AbsoluteUri_SkipsTransparentRewrite()
    {
        using var backendListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        backendListener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        backendListener.Listen(1);
        var backendPort = ((IPEndPoint)backendListener.LocalEndPoint!).Port;

        var backendRequestLineTask = AcceptBackendRequestLineAndRespondAsync(backendListener);

        var config = Configuration.Default with
        {
            Verbose = false,
            IsTransparentProxyEnabled = true
        };

        var response = await SendRequestAsync(
            config,
            $"GET http://127.0.0.1:{backendPort}/absolute HTTP/1.1\r\nHost: 127.0.0.1:{backendPort}\r\nConnection: close\r\n\r\n");

        var backendRequestLine = await backendRequestLineTask;

        Assert.Contains("200 OK", response, StringComparison.Ordinal);
        Assert.Equal("GET /absolute HTTP/1.1", backendRequestLine);
    }

    [Fact]
    public async Task ProcessAsync_TransparentProxy_ConnectRequest_SkipsTransparentRewrite()
    {
        using var targetListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        targetListener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        targetListener.Listen(1);
        var targetPort = ((IPEndPoint)targetListener.LocalEndPoint!).Port;

        var acceptAndCloseTask = AcceptAndCloseAsync(targetListener);

        var config = Configuration.Default with
        {
            Verbose = false,
            IsTransparentProxyEnabled = true
        };

        var response = await SendRequestAsync(
            config,
            $"CONNECT 127.0.0.1:{targetPort} HTTP/1.1\r\nHost: 127.0.0.1:{targetPort}\r\n\r\n");

        await acceptAndCloseTask;

        Assert.Contains("200 Connection established", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessAsync_DirectHttpTargetConnectionRefused_ReturnsInternalServerError()
    {
        var targetPort = GetUnusedTcpPort();
        var response = await SendRequestAsync(
            Configuration.Default with { Verbose = false },
            $"GET http://127.0.0.1:{targetPort}/ HTTP/1.1\r\nHost: 127.0.0.1:{targetPort}\r\nConnection: close\r\n\r\n");

        Assert.Contains("500 Internal Server Error", response, StringComparison.Ordinal);
        Assert.DoesNotContain("502 Bad Gateway", response, StringComparison.Ordinal);
        Assert.DoesNotContain("504 Gateway Timeout", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_DirectConnectTargetConnectionRefused_ReturnsInternalServerError()
    {
        var targetPort = GetUnusedTcpPort();
        var response = await SendRequestAsync(
            Configuration.Default with { Verbose = false },
            $"CONNECT 127.0.0.1:{targetPort} HTTP/1.1\r\nHost: 127.0.0.1:{targetPort}\r\n\r\n");

        Assert.Contains("500 Internal Server Error", response, StringComparison.Ordinal);
        Assert.DoesNotContain("502 Bad Gateway", response, StringComparison.Ordinal);
        Assert.DoesNotContain("504 Gateway Timeout", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_ProxyRequest_DoesNotAcceptAuthorizationHeaderOnly()
    {
        var config = BuildAuthConfig();
        var auth = BuildBasicToken("alice", "secret");

        var response = await SendRequestAsync(
            config,
            $"GET http://example.com/ HTTP/1.1\r\nHost: example.com\r\nAuthorization: Basic {auth}\r\n\r\n");

        Assert.Contains("407 Proxy Authentication Required", response, StringComparison.Ordinal);
        Assert.Contains("Proxy-Authenticate: Basic realm=\"TinyProxy\"", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_StatHostRequest_UsesAuthorizationAndReturns401WhenMissing()
    {
        var config = BuildAuthConfig();

        var response = await SendRequestAsync(
            config,
            "GET / HTTP/1.1\r\nHost: stats.local\r\n\r\n");

        Assert.Contains("401 Unauthorized", response, StringComparison.Ordinal);
        Assert.Contains("WWW-Authenticate: Basic realm=\"TinyProxy\"", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_StatHostRequest_PrefersProxyAuthorizationOverAuthorization()
    {
        var config = BuildAuthConfig();
        var validAuth = BuildBasicToken("alice", "secret");

        var response = await SendRequestAsync(
            config,
            $"GET / HTTP/1.1\r\nHost: stats.local\r\nProxy-Authorization: Basic invalid\r\nAuthorization: Basic {validAuth}\r\n\r\n");

        Assert.Contains("407 Proxy Authentication Required", response, StringComparison.Ordinal);
        Assert.DoesNotContain("200 OK", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_StatHostRequest_WithPortSuffix_UsesStatAuthFlow()
    {
        var config = BuildAuthConfig();

        var response = await SendRequestAsync(
            config,
            "GET / HTTP/1.1\r\nHost: stats.local:8080\r\n\r\n");

        Assert.Contains("401 Unauthorized", response, StringComparison.Ordinal);
        Assert.Contains("WWW-Authenticate: Basic realm=\"TinyProxy\"", response, StringComparison.Ordinal);
        Assert.DoesNotContain("407 Proxy Authentication Required", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_FilterDefaultDenyWithoutConfiguredFilter_DoesNotReturnForbidden()
    {
        using var backendListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        backendListener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        backendListener.Listen(1);
        var backendPort = ((IPEndPoint)backendListener.LocalEndPoint!).Port;

        var backendRequestLineTask = AcceptBackendRequestLineAndRespondAsync(backendListener);

        var config = Configuration.Default with
        {
            Verbose = false,
            FilterDefaultDeny = true
        };

        var response = await SendRequestAsync(
            config,
            $"GET http://127.0.0.1:{backendPort}/allowed HTTP/1.1\r\nHost: 127.0.0.1:{backendPort}\r\nConnection: close\r\n\r\n");

        var backendRequestLine = await backendRequestLineTask;

        Assert.Contains("200 OK", response, StringComparison.Ordinal);
        Assert.DoesNotContain("403 Forbidden", response, StringComparison.Ordinal);
        Assert.Equal("GET /allowed HTTP/1.1", backendRequestLine);
    }

    [Fact]
    public async Task ProcessAsync_ConnectDisallowedPort_TakesPrecedenceOverUrlFilter()
    {
        var config = Configuration.Default with
        {
            Verbose = false,
            AllowedConnectPorts = new HashSet<ushort> { 443 },
            FilterPatterns = new List<string> { "blocked\\.example\\.com" }
        };

        var response = await SendRequestAsync(
            config,
            "CONNECT blocked.example.com:8443 HTTP/1.1\r\nHost: blocked.example.com:8443\r\n\r\n");

        Assert.Contains("403 Forbidden", response, StringComparison.Ordinal);
        Assert.Contains("Port 8443 is not allowed for CONNECT", response, StringComparison.Ordinal);
        Assert.DoesNotContain("URL filtered by proxy policy", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_ReverseProxy_RewriteHappensBeforeFilterEvaluation()
    {
        var backendPort = GetUnusedTcpPort();
        var config = Configuration.Default with
        {
            Verbose = false,
            IsReverseProxyEnabled = true,
            ReversePaths = new List<ReversePathConfig>
            {
                new() { Path = "/app/", Url = $"http://127.0.0.1:{backendPort}/base/" }
            },
            FilterPatterns = new List<string> { "127\\.0\\.0\\.1" }
        };

        var response = await SendRequestAsync(
            config,
            "GET /app/test HTTP/1.1\r\nHost: proxy.local\r\nConnection: close\r\n\r\n");

        Assert.Contains("403 Forbidden", response, StringComparison.Ordinal);
        Assert.Contains("URL filtered by proxy policy", response, StringComparison.Ordinal);
        Assert.DoesNotContain("500 Internal Server Error", response, StringComparison.Ordinal);
    }

    private static Configuration BuildAuthConfig()
    {
        return new Configuration
        {
            StatHost = "stats.local",
            BasicAuth = new BasicAuthConfig { Username = "alice", Password = "secret" },
            Verbose = false
        };
    }

    private static async Task<string> SendRequestAsync(Configuration config, string rawRequest)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);
        using var server = await listener.AcceptAsync();
        await connectTask;

        var logger = new NullLogger();
        var stats = new Stats();
        using var accessLogger = new AccessLogger(config, logger);
        var loopDetector = new LoopDetector();

        using var connection = new Connection(server, logger, config, stats, accessLogger, loopDetector);

        var requestBytes = Encoding.ASCII.GetBytes(rawRequest);
        await client.SendAsync(requestBytes, SocketFlags.None);
        client.Shutdown(SocketShutdown.Send);

        await connection.ProcessAsync();
        connection.Dispose();

        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (true)
        {
            int read;
            try
            {
                read = await client.ReceiveAsync(buffer, SocketFlags.None, cts.Token);
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

    private static int GetUnusedTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<string> AcceptBackendRequestLineAndRespondAsync(Socket listener)
    {
        using var server = await listener.AcceptAsync();

        var received = new MemoryStream();
        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (true)
        {
            var read = await server.ReceiveAsync(buffer, SocketFlags.None, cts.Token);
            if (read <= 0) break;
            received.Write(buffer, 0, read);

            var text = Encoding.ASCII.GetString(received.GetBuffer(), 0, (int)received.Length);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }

        var requestText = Encoding.ASCII.GetString(received.GetBuffer(), 0, (int)received.Length);
        var requestLine = requestText.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];

        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
        await server.SendAllAsync(response, cts.Token);

        return requestLine;
    }

    private static async Task AcceptAndCloseAsync(Socket listener)
    {
        using var server = await listener.AcceptAsync();
    }

    private static string BuildBasicToken(string username, string password)
    {
        var raw = $"{username}:{password}";
        return Convert.ToBase64String(Encoding.ASCII.GetBytes(raw));
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
