namespace TinyProxy.Tests.Protocol;

public class HttpForwarderTargetResolutionTests
{
    private static readonly MethodInfo s_tryResolveTargetMethod =
        typeof(HttpForwarder).GetMethod(
            "TryResolveTarget",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public void TryResolveTarget_UnsupportedAbsoluteScheme_DoesNotFallbackToHostHeader()
    {
        var forwarder = CreateForwarder(Configuration.Default with { Verbose = false });
        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Get,
            Uri = "gopher://example.com/resource",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("example.com"u8.ToArray())
            }
        };

        var (ok, _, _, unsupported) = InvokeTryResolveTarget(forwarder, request);

        Assert.False(ok);
        Assert.True(unsupported);
    }

    [Fact]
    public void TryResolveTarget_HttpsAbsoluteUri_IsUnsupportedLikeTinyproxyUpstream()
    {
        var forwarder = CreateForwarder(Configuration.Default with { Verbose = false });
        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Get,
            Uri = "https://example.com/secure",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("example.com"u8.ToArray())
            }
        };

        var (ok, _, _, unsupported) = InvokeTryResolveTarget(forwarder, request);

        Assert.False(ok);
        Assert.True(unsupported);
    }

    [Fact]
    public void TryResolveTarget_FtpAbsoluteUri_WithUpstreamConfigured_ResolvesHostAndPort()
    {
        var config = Configuration.Default with
        {
            Verbose = false,
            UpstreamProxy = new UpstreamProxyConfig
            {
                Host = "upstream.local",
                Port = 3128,
                Type = UpstreamProxyType.Http
            }
        };
        var forwarder = CreateForwarder(config);
        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Get,
            Uri = "ftp://ftp.example.com:2121/pub/file.txt",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("ignored.example"u8.ToArray())
            }
        };

        var (ok, host, port, unsupported) = InvokeTryResolveTarget(forwarder, request);

        Assert.True(ok);
        Assert.False(unsupported);
        Assert.Equal("ftp.example.com", host);
        Assert.Equal(2121, port);
    }

    [Fact]
    public void TryResolveTarget_FtpAbsoluteUri_WithUpstreamRulesConfigured_ResolvesHostAndPort()
    {
        var config = Configuration.Default with
        {
            Verbose = false,
            UpstreamProxyRules = new List<UpstreamProxyRuleConfig>
            {
                new()
                {
                    Domain = ".corp.example",
                    Proxy = new UpstreamProxyConfig
                    {
                        Host = "socks.corp",
                        Port = 1080,
                        Type = UpstreamProxyType.Socks5
                    }
                }
            }
        };
        var forwarder = CreateForwarder(config);
        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Get,
            Uri = "ftp://mirror.corp.example/pub/file.txt",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("ignored.example"u8.ToArray())
            }
        };

        var (ok, host, port, unsupported) = InvokeTryResolveTarget(forwarder, request);

        Assert.True(ok);
        Assert.False(unsupported);
        Assert.Equal("mirror.corp.example", host);
        Assert.Equal(80, port);
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

    private static (bool ok, string host, int port, bool unsupported) InvokeTryResolveTarget(
        HttpForwarder forwarder,
        HttpRequest request)
    {
        var args = new object?[] { request, null, 0, false };
        var ok = (bool)s_tryResolveTargetMethod.Invoke(forwarder, args)!;
        var host = (string)args[1]!;
        var port = (int)args[2]!;
        var unsupported = (bool)args[3]!;
        return (ok, host, port, unsupported);
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
