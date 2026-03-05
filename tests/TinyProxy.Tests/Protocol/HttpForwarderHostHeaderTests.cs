namespace TinyProxy.Tests.Protocol;

public class HttpForwarderHostHeaderTests
{
    private static readonly MethodInfo s_buildForwardRequestMethod =
        typeof(HttpForwarder).GetMethod("BuildForwardRequest", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public void BuildForwardRequest_RewritesHostHeader_FromParsedTarget()
    {
        var forwarder = CreateForwarder();
        var request = CreateRequestWithHost("evil.example.com");

        var payload = BuildPayload(forwarder, request, "good.example.com", 80, useAbsoluteUri: false);

        Assert.Contains("Host: good.example.com\r\n", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Host: evil.example.com\r\n", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildForwardRequest_OmitsDefaultPort_FromHostHeader()
    {
        var forwarder = CreateForwarder();
        var request = CreateRequestWithHost("ignored.example.com");

        var payload80 = BuildPayload(forwarder, request, "example.com", 80, useAbsoluteUri: false);
        var payload443 = BuildPayload(forwarder, request, "example.com", 443, useAbsoluteUri: false);

        Assert.Contains("Host: example.com\r\n", payload80, StringComparison.Ordinal);
        Assert.DoesNotContain("Host: example.com:80\r\n", payload80, StringComparison.Ordinal);

        Assert.Contains("Host: example.com\r\n", payload443, StringComparison.Ordinal);
        Assert.DoesNotContain("Host: example.com:443\r\n", payload443, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildForwardRequest_AppendsNonDefaultPort_AndBracketsIpv6()
    {
        var forwarder = CreateForwarder();
        var request = CreateRequestWithHost("ignored.example.com");

        var payload = BuildPayload(forwarder, request, "2001:db8::1", 8080, useAbsoluteUri: false);

        Assert.Contains("Host: [2001:db8::1]:8080\r\n", payload, StringComparison.Ordinal);
    }

    private static HttpForwarder CreateForwarder()
    {
        var config = Configuration.Default with
        {
            AddViaHeader = false,
            Verbose = false
        };

        return new HttpForwarder(
            new NullLogger(),
            config,
            new Stats(),
            new AccessLogger(config, new NullLogger()),
            "127.0.0.1");
    }

    private static HttpRequest CreateRequestWithHost(string host)
    {
        return new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Get,
            Uri = "/",
            Version = "HTTP/1.1",
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes(host))
            },
            Body = ReadOnlySequence<byte>.Empty
        };
    }

    private static string BuildPayload(HttpForwarder forwarder, HttpRequest request, string host, int port, bool useAbsoluteUri)
    {
        var bytes = (byte[])s_buildForwardRequestMethod.Invoke(
            forwarder,
            new object[] { request, host, port, useAbsoluteUri })!;

        return Encoding.ASCII.GetString(bytes);
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
