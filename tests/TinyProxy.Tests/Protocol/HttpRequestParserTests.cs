namespace TinyProxy.Tests.Protocol;

public class HttpRequestParserTests
{
    private readonly HttpRequestParser _parser = new(new NullLogger());

    [Fact]
    public void TryParseRequest_SupportsLfOnlyLineEndings()
    {
        var raw = "POST /upload?q=1 HTTP/1.1\nHost: example.com\nUser-Agent: test\nContent-Length: 5\n\nabcde"u8.ToArray();
        ReadOnlySequence<byte> sequence = new(raw);

        var ok = _parser.TryParseRequest(ref sequence, out var request);

        Assert.True(ok);
        Assert.NotNull(request);
        Assert.Equal(TinyProxy.Protocol.Http.HttpMethod.Post, request!.Method);
        Assert.Equal("/upload?q=1", request.Uri);
        Assert.Equal("HTTP/1.1", request.Version);
        Assert.Equal("example.com", request.Host);
        Assert.Equal("test", request.UserAgent);
        Assert.Equal(5, request.ContentLength);
        Assert.Equal("abcde", request.GetBodyAsAsciiString());
    }

    [Fact]
    public void TryParseRequest_SupportsCrLfLineEndings()
    {
        var raw = "GET / HTTP/1.1\r\nHost: example.com\r\n\r\n"u8.ToArray();
        ReadOnlySequence<byte> sequence = new(raw);

        var ok = _parser.TryParseRequest(ref sequence, out var request);

        Assert.True(ok);
        Assert.NotNull(request);
        Assert.Equal(TinyProxy.Protocol.Http.HttpMethod.Get, request!.Method);
        Assert.Equal("/", request.Uri);
        Assert.Equal("example.com", request.Host);
        Assert.Equal(0, request.Body.Length);
    }

    [Fact]
    public void TryParseRequest_ReturnsFalse_ForMalformedRequestLine()
    {
        var raw = "GET_ONLY\nHost: example.com\n\n"u8.ToArray();
        ReadOnlySequence<byte> sequence = new(raw);

        var ok = _parser.TryParseRequest(ref sequence, out var request);

        Assert.False(ok);
        Assert.Null(request);
    }

    [Fact]
    public void TryParseRequest_AcceptsRequestLine_WithMultipleSpacesLikeTinyproxySscanf()
    {
        var raw = "GET   /multi-space   HTTP/1.1\r\nHost: example.com\r\n\r\n"u8.ToArray();
        ReadOnlySequence<byte> sequence = new(raw);

        var ok = _parser.TryParseRequest(ref sequence, out var request);

        Assert.True(ok);
        Assert.NotNull(request);
        Assert.Equal(TinyProxy.Protocol.Http.HttpMethod.Get, request!.Method);
        Assert.Equal("/multi-space", request.Uri);
        Assert.Equal("HTTP/1.1", request.Version);
    }

    [Fact]
    public void TryParseRequest_AcceptsRequestLine_WithExtraTokenLikeTinyproxySscanf()
    {
        var raw = "GET / HTTP/1.1 EXTRA\r\nHost: example.com\r\n\r\n"u8.ToArray();
        ReadOnlySequence<byte> sequence = new(raw);

        var ok = _parser.TryParseRequest(ref sequence, out var request);

        Assert.True(ok);
        Assert.NotNull(request);
        Assert.Equal("HTTP/1.1", request!.Version);
    }

    [Theory]
    [InlineData("GET / NOTHTTP/1.1\r\nHost: example.com\r\n\r\n")]
    [InlineData("GET / HTTP/1\r\nHost: example.com\r\n\r\n")]
    [InlineData("GET / HTTP/x.1\r\nHost: example.com\r\n\r\n")]
    public void TryParseRequest_RejectsMalformedHttpVersionToken(string rawRequest)
    {
        ReadOnlySequence<byte> sequence = new(Encoding.ASCII.GetBytes(rawRequest));

        var ok = _parser.TryParseRequest(ref sequence, out var request);

        Assert.False(ok);
        Assert.Null(request);
    }

    [Fact]
    public void TryParseRequest_SkipsLeadingBlankLines_LikeTinyproxyUpstream()
    {
        var raw = "\r\n\r\nGET / HTTP/1.1\r\nHost: example.com\r\n\r\n"u8.ToArray();
        ReadOnlySequence<byte> sequence = new(raw);

        var ok = _parser.TryParseRequest(ref sequence, out var request);

        Assert.True(ok);
        Assert.NotNull(request);
        Assert.Equal(TinyProxy.Protocol.Http.HttpMethod.Get, request!.Method);
        Assert.Equal("/", request.Uri);
        Assert.Equal("example.com", request.Host);
    }

    [Fact]
    public void TryParseRequest_IgnoresMalformedHeader_InsteadOfFailing()
    {
        var raw = "GET / HTTP/1.1\r\nHost: example.com\r\nX-Bad-Header\r\nUser-Agent: parser-test\r\n\r\n"u8.ToArray();
        ReadOnlySequence<byte> sequence = new(raw);

        var ok = _parser.TryParseRequest(ref sequence, out var request);

        Assert.True(ok);
        Assert.NotNull(request);
        Assert.Equal("example.com", request!.Host);
        Assert.Equal("parser-test", request.UserAgent);
        Assert.False(request.HasHeader("X-Bad-Header"));
    }

    [Fact]
    public void TryParseRequest_SupportsFoldedHeaderContinuation()
    {
        var raw = "GET / HTTP/1.1\r\nHost: example.com\r\nX-Test: one\r\n\ttwo\r\n three\r\n\r\n"u8.ToArray();
        ReadOnlySequence<byte> sequence = new(raw);

        var ok = _parser.TryParseRequest(ref sequence, out var request);

        Assert.True(ok);
        Assert.NotNull(request);
        Assert.Equal("one two three", request!.GetHeader("X-Test"));
    }

    [Fact]
    public void TryParseRequest_SupportsHttp09GetRequestLine()
    {
        var raw = "GET /legacy\r\n\r\n"u8.ToArray();
        ReadOnlySequence<byte> sequence = new(raw);

        var ok = _parser.TryParseRequest(ref sequence, out var request);

        Assert.True(ok);
        Assert.NotNull(request);
        Assert.Equal(TinyProxy.Protocol.Http.HttpMethod.Get, request!.Method);
        Assert.Equal("/legacy", request.Uri);
        Assert.Equal("HTTP/0.9", request.Version);
    }

    [Fact]
    public void TryParseRequest_RejectsHttp09StyleForNonGet()
    {
        var raw = "POST /legacy\r\n\r\n"u8.ToArray();
        ReadOnlySequence<byte> sequence = new(raw);

        var ok = _parser.TryParseRequest(ref sequence, out var request);

        Assert.False(ok);
        Assert.Null(request);
    }

    [Fact]
    public void TryParseRequest_AcceptsUnknownMethod_ForHttp11LikeTinyproxy()
    {
        var raw = "PROPFIND /dav HTTP/1.1\r\nHost: example.com\r\n\r\n"u8.ToArray();
        ReadOnlySequence<byte> sequence = new(raw);

        var ok = _parser.TryParseRequest(ref sequence, out var request);

        Assert.True(ok);
        Assert.NotNull(request);
        Assert.Equal(TinyProxy.Protocol.Http.HttpMethod.None, request!.Method);
        Assert.Equal("PROPFIND", request.RawMethod);
        Assert.Equal("/dav", request.Uri);
    }

    [Fact]
    public void TryParseRequest_DuplicateHeaders_KeepFirstValueLikeTinyproxyLookup()
    {
        var raw =
            "GET / HTTP/1.1\r\n" +
            "Host: first.example\r\n" +
            "Host: second.example\r\n" +
            "Content-Length: 1\r\n" +
            "Content-Length: 9\r\n\r\nx";
        ReadOnlySequence<byte> sequence = new(Encoding.ASCII.GetBytes(raw));

        var ok = _parser.TryParseRequest(ref sequence, out var request);

        Assert.True(ok);
        Assert.NotNull(request);
        Assert.Equal("first.example", request!.Host);
        Assert.Equal("first.example", request.GetHeader("Host"));
        Assert.Equal(1, request.ContentLength);
        Assert.Equal("x", request.GetBodyAsAsciiString());
        Assert.Equal(4, request.HeaderLines.Count);
        Assert.Equal("Host", request.HeaderLines[0].Key);
        Assert.Equal("first.example", Encoding.ASCII.GetString(request.HeaderLines[0].Value.ToArray()));
        Assert.Equal("Host", request.HeaderLines[1].Key);
        Assert.Equal("second.example", Encoding.ASCII.GetString(request.HeaderLines[1].Value.ToArray()));
    }

    [Fact]
    public void TryParseRequest_HeaderLimit_AlignsWithTinyproxyMaxHeaders()
    {
        Assert.Equal(10000, TinyProxy.Core.ProxyConstants.MaxHeaders);
    }

    [Fact]
    public void TryParseRequest_StoredHeaderLimit_AlignsWithTinyproxyPseudomapMaxSize()
    {
        Assert.Equal(256, TinyProxy.Core.ProxyConstants.MaxStoredHeaders);
    }

    [Fact]
    public void TryParseRequest_AcceptsHeaderCount_AtConfiguredLimit()
    {
        var sb = new StringBuilder();
        sb.Append("GET / HTTP/1.1\r\n");
        for (var i = 0; i < TinyProxy.Core.ProxyConstants.MaxHeaders - 1; i++)
            sb.Append("X-H").Append(i).Append(": v\r\n");
        sb.Append("\r\n");

        ReadOnlySequence<byte> sequence = new(Encoding.ASCII.GetBytes(sb.ToString()));
        var ok = _parser.TryParseRequest(ref sequence, out var request);

        Assert.True(ok);
        Assert.NotNull(request);
    }

    [Fact]
    public void TryParseRequest_RejectsHeaderCount_ExceedingConfiguredLimit()
    {
        var sb = new StringBuilder();
        sb.Append("GET / HTTP/1.1\r\n");
        for (var i = 0; i < TinyProxy.Core.ProxyConstants.MaxHeaders; i++)
            sb.Append("X-H").Append(i).Append(": v\r\n");
        sb.Append("\r\n");

        ReadOnlySequence<byte> sequence = new(Encoding.ASCII.GetBytes(sb.ToString()));
        var ok = _parser.TryParseRequest(ref sequence, out var request);

        Assert.False(ok);
        Assert.Null(request);
    }

    [Fact]
    public void TryParseRequest_StopsStoringHeaders_AfterConfiguredStoredLimit_ButContinuesParsing()
    {
        var sb = new StringBuilder();
        sb.Append("POST / HTTP/1.1\r\n");
        sb.Append("Host: example.com\r\n");
        for (var i = 0; i < TinyProxy.Core.ProxyConstants.MaxStoredHeaders - 1; i++)
            sb.Append("X-H").Append(i).Append(": v\r\n");
        sb.Append("X-Overflow: dropped\r\n");
        sb.Append("Content-Length: 3\r\n");
        sb.Append("\r\nabc");

        ReadOnlySequence<byte> sequence = new(Encoding.ASCII.GetBytes(sb.ToString()));
        var ok = _parser.TryParseRequest(ref sequence, out var request);

        Assert.True(ok);
        Assert.NotNull(request);
        Assert.Equal(TinyProxy.Core.ProxyConstants.MaxStoredHeaders, request!.HeaderLines.Count);
        Assert.Equal("example.com", request.Host);
        Assert.False(request.HasHeader("X-Overflow"));
        Assert.False(request.HasHeader("Content-Length"));
        Assert.Null(request.ContentLength);
        Assert.Equal("abc", request.GetBodyAsAsciiString());
    }

    [Fact]
    public void TryParseRequest_RejectsExcessiveContinuationLines_ByLineCountLikeTinyproxy()
    {
        var sb = new StringBuilder();
        sb.Append("GET / HTTP/1.1\r\n");
        sb.Append("X-Test: value\r\n");
        for (var i = 0; i < TinyProxy.Core.ProxyConstants.MaxHeaders; i++)
            sb.Append(" more\r\n");
        sb.Append("\r\n");

        ReadOnlySequence<byte> sequence = new(Encoding.ASCII.GetBytes(sb.ToString()));
        var ok = _parser.TryParseRequest(ref sequence, out var request);

        Assert.False(ok);
        Assert.Null(request);
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

internal static class HttpRequestParserTestExtensions
{
    public static string GetBodyAsAsciiString(this HttpRequest request)
    {
        var span = request.Body.IsSingleSegment ? request.Body.FirstSpan : request.Body.ToArray();
        return System.Text.Encoding.ASCII.GetString(span);
    }
}
