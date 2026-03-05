namespace TinyProxy.Tests.Protocol;

public class TransparentProxyTests
{
    [Fact]
    public void GetTransparentDestination_HostHeaderIsLocalAddress_ReturnsNull()
    {
        var config = Configuration.Default with
        {
            Verbose = false,
            ListenAddress = "127.0.0.1"
        };

        var proxy = new TransparentProxy(new NullLogger(), config);
        using var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var request = CreateRequest("127.0.0.1:8080");

        var destination = proxy.GetTransparentDestination(clientSocket, request);

        Assert.Null(destination);
    }

    [Fact]
    public void GetTransparentDestination_HostHeaderIsRemoteAddress_ReturnsParsedHostAndPort()
    {
        var config = Configuration.Default with
        {
            Verbose = false,
            ListenAddress = "127.0.0.1"
        };

        var proxy = new TransparentProxy(new NullLogger(), config);
        using var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var request = CreateRequest("198.51.100.20:8080");

        var destination = proxy.GetTransparentDestination(clientSocket, request);

        Assert.NotNull(destination);
        Assert.Equal("198.51.100.20", destination.Value.host);
        Assert.Equal(8080, destination.Value.port);
    }

    [Fact]
    public void GetTransparentDestination_HostHeaderIsUppercaseLocalhost_ReturnsNull()
    {
        var config = Configuration.Default with
        {
            Verbose = false,
            ListenAddress = "127.0.0.1"
        };

        var proxy = new TransparentProxy(new NullLogger(), config);
        using var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var request = CreateRequest("LOCALHOST:8080");

        var destination = proxy.GetTransparentDestination(clientSocket, request);

        Assert.Null(destination);
    }

    [Fact]
    public void BuildAbsoluteUri_DefaultPort_PreservesExplicitPort80()
    {
        var proxy = new TransparentProxy(new NullLogger(), Configuration.Default with { Verbose = false });
        var absolute = proxy.BuildAbsoluteUri("/path?x=1", "example.com", 80, "/path?x=1");
        Assert.Equal("http://example.com:80/path?x=1", absolute);
    }

    [Fact]
    public void BuildAbsoluteUri_NonDefaultPort_PreservesExplicitPort()
    {
        var proxy = new TransparentProxy(new NullLogger(), Configuration.Default with { Verbose = false });
        var absolute = proxy.BuildAbsoluteUri("/path", "example.com", 8080, "/path");
        Assert.Equal("http://example.com:8080/path", absolute);
    }

    [Fact]
    public void BuildAbsoluteUri_Ipv6Host_DefaultPort_UsesBracketedHost()
    {
        var proxy = new TransparentProxy(new NullLogger(), Configuration.Default with { Verbose = false });
        var absolute = proxy.BuildAbsoluteUri("/path", "2001:db8::1", 80, "/path");
        Assert.Equal("http://[2001:db8::1]:80/path", absolute);
    }

    [Fact]
    public void BuildAbsoluteUri_Ipv6Host_NonDefaultPort_UsesBracketedHost()
    {
        var proxy = new TransparentProxy(new NullLogger(), Configuration.Default with { Verbose = false });
        var absolute = proxy.BuildAbsoluteUri("/path", "2001:db8::1", 8080, "/path");
        Assert.Equal("http://[2001:db8::1]:8080/path", absolute);
    }

    private static HttpRequest CreateRequest(string hostHeader)
    {
        return new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Get,
            Uri = "/path",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes(hostHeader))
            },
            Host = hostHeader
        };
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
