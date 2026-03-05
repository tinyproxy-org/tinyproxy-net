namespace TinyProxy.Tests.Protocol;

public class HttpRequestTests
{
    [Fact]
    public void GetHeader_ReturnsDecodedAsciiValue()
    {
        var request = new HttpRequest
        {
            Headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = new ReadOnlySequence<byte>("example.com"u8.ToArray())
            }
        };

        var value = request.GetHeader("host");

        Assert.Equal("example.com", value);
    }

    [Fact]
    public void WithBody_ReturnsNewRequestWithUpdatedBody()
    {
        var originalBody = new ReadOnlySequence<byte>("a=1"u8.ToArray());
        var newBody = new ReadOnlySequence<byte>("a=2"u8.ToArray());
        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.Post,
            Uri = "/submit",
            Body = originalBody
        };

        var updated = request.WithBody(newBody);

        Assert.Equal("a=1", request.GetBodyAsString());
        Assert.Equal("a=2", updated.GetBodyAsString());
        Assert.Same(request.Headers, updated.Headers);
    }

    [Fact]
    public void TryGetTarget_Uses443AsDefaultPort_ForHttpsAbsoluteUri()
    {
        var request = new HttpRequest
        {
            Uri = "https://example.com/secure"
        };

        var ok = request.TryGetTarget(out var host, out var port);

        Assert.True(ok);
        Assert.Equal("example.com", host);
        Assert.Equal(443, port);
    }

    [Fact]
    public void TryGetTarget_StripsUserInfo_FromAbsoluteUri()
    {
        var request = new HttpRequest
        {
            Uri = "http://user:pass@example.com:8080/path"
        };

        var ok = request.TryGetTarget(out var host, out var port);

        Assert.True(ok);
        Assert.Equal("example.com", host);
        Assert.Equal(8080, port);
    }

    [Fact]
    public void TryGetTarget_StripsFromFirstAt_InAbsoluteUriLikeTinyproxyUpstream()
    {
        var request = new HttpRequest
        {
            Uri = "http://user@realm@example.com:8080/path"
        };

        var ok = request.TryGetTarget(out var host, out var port);

        Assert.True(ok);
        Assert.Equal("realm@example.com", host);
        Assert.Equal(8080, port);
    }

    [Fact]
    public void TryGetTarget_AbsoluteUriWithInvalidPort_FallsBackToDefaultPort()
    {
        var request = new HttpRequest
        {
            Uri = "http://example.com:notaport/path"
        };

        var ok = request.TryGetTarget(out var host, out var port);

        Assert.True(ok);
        Assert.Equal("example.com", host);
        Assert.Equal(80, port);
    }

    [Fact]
    public void GetMethodToken_PrefersRawMethod_WhenProvided()
    {
        var request = new HttpRequest
        {
            Method = TinyProxy.Protocol.Http.HttpMethod.None,
            RawMethod = "PROPFIND"
        };

        Assert.Equal("PROPFIND", request.GetMethodToken());
    }
}

internal static class HttpRequestTestExtensions
{
    public static string GetBodyAsString(this HttpRequest request)
    {
        var span = request.Body.IsSingleSegment ? request.Body.FirstSpan : request.Body.ToArray();
        return System.Text.Encoding.ASCII.GetString(span);
    }
}
