namespace TinyProxy.Tests.Protocol;

public class HttpForwarderResponseParsingTests
{
    private static readonly MethodInfo s_parseResponseHeaderInfoMethod =
        typeof(HttpForwarder).GetMethod(
            "ParseResponseHeaderInfo",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo s_determineResponseBodyModeMethod =
        typeof(HttpForwarder).GetMethod(
            "DetermineResponseBodyMode",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo s_isInterimResponseStatusCodeMethod =
        typeof(HttpForwarder).GetMethod(
            "IsInterimResponseStatusCode",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void ParseResponseHeaderInfo_ParsesContentLengthResponse()
    {
        var bytes = "HTTP/1.1 200 OK\r\nContent-Length: 12\r\nConnection: keep-alive\r\n\r\n"u8.ToArray();

        var result = InvokeParseResponseHeaderInfo(bytes);

        Assert.Equal(200, result.statusCode);
        Assert.False(result.isChunked);
        Assert.Equal(12, result.contentLength);
    }

    [Fact]
    public void ParseResponseHeaderInfo_ParsesChunkedResponse_WithLfOnly()
    {
        var bytes = "HTTP/1.1 200 OK\nTransfer-Encoding: gzip, chunked\n\n"u8.ToArray();

        var result = InvokeParseResponseHeaderInfo(bytes);

        Assert.Equal(200, result.statusCode);
        Assert.True(result.isChunked);
        Assert.Null(result.contentLength);
    }

    [Fact]
    public void ParseResponseHeaderInfo_ParsesChunkedResponse_WithMixedCaseHeaderName()
    {
        var bytes = "HTTP/1.1 200 OK\r\ntrAnSfEr-EnCoDiNg: chunked\r\n\r\n"u8.ToArray();

        var result = InvokeParseResponseHeaderInfo(bytes);

        Assert.Equal(200, result.statusCode);
        Assert.True(result.isChunked);
        Assert.Null(result.contentLength);
    }

    [Fact]
    public void ParseResponseHeaderInfo_ParsesChunkedResponse_WithFoldedTransferEncoding()
    {
        var bytes = "HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip,\r\n chunked\r\n\r\n"u8.ToArray();

        var result = InvokeParseResponseHeaderInfo(bytes);

        Assert.Equal(200, result.statusCode);
        Assert.True(result.isChunked);
        Assert.Null(result.contentLength);
    }

    [Fact]
    public void ParseResponseHeaderInfo_ParsesContentLengthResponse_WithFoldedContinuation()
    {
        var bytes = "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\tignored\r\n\r\n"u8.ToArray();

        var result = InvokeParseResponseHeaderInfo(bytes);

        Assert.Equal(200, result.statusCode);
        Assert.False(result.isChunked);
        Assert.Equal(5, result.contentLength);
    }

    [Fact]
    public void ParseResponseHeaderInfo_IgnoresHeadersAfterDoubleCgiStatusLine()
    {
        var bytes = "HTTP/1.1 200 OK\r\nHTTP/1.1 200 CGI\r\nContent-Length: 999\r\nTransfer-Encoding: chunked\r\n\r\n"u8.ToArray();

        var result = InvokeParseResponseHeaderInfo(bytes);

        Assert.Equal(200, result.statusCode);
        Assert.False(result.isChunked);
        Assert.Null(result.contentLength);
    }

    [Fact]
    public void ParseResponseHeaderInfo_SkipsLeadingBlankLines()
    {
        var bytes = "\r\n\r\nHTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n"u8.ToArray();

        var result = InvokeParseResponseHeaderInfo(bytes);

        Assert.Equal(200, result.statusCode);
        Assert.False(result.isChunked);
        Assert.Equal(5, result.contentLength);
    }

    [Fact]
    public void ParseResponseHeaderInfo_RejectsInvalidStatusLine()
    {
        var bytes = "NOT_A_STATUS_LINE\r\nContent-Length: 1\r\n\r\n"u8.ToArray();
        var ex = Assert.Throws<TargetInvocationException>(() => InvokeParseResponseHeaderInfo(bytes));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Theory]
    [InlineData(TinyProxy.Protocol.Http.HttpMethod.Head, 200, false, 10L, "None")]
    [InlineData(TinyProxy.Protocol.Http.HttpMethod.Get, 204, false, null, "None")]
    [InlineData(TinyProxy.Protocol.Http.HttpMethod.Get, 101, false, null, "UpgradedTunnel")]
    [InlineData(TinyProxy.Protocol.Http.HttpMethod.Get, 200, true, null, "Chunked")]
    [InlineData(TinyProxy.Protocol.Http.HttpMethod.Get, 200, false, 42L, "ContentLength")]
    [InlineData(TinyProxy.Protocol.Http.HttpMethod.Get, 200, false, null, "UntilClose")]
    public void DetermineResponseBodyMode_ReturnsExpectedMode(
        TinyProxy.Protocol.Http.HttpMethod method,
        int statusCode,
        bool isChunked,
        long? contentLength,
        string expectedMode)
    {
        var result = s_determineResponseBodyModeMethod.Invoke(
            null,
            new object?[] { method, statusCode, isChunked, contentLength });

        Assert.NotNull(result);
        Assert.Equal(expectedMode, result!.ToString());
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(103, true)]
    [InlineData(101, false)]
    [InlineData(200, false)]
    public void IsInterimResponseStatusCode_ReturnsExpectedValue(int statusCode, bool expected)
    {
        var result = (bool)s_isInterimResponseStatusCodeMethod.Invoke(null, new object[] { statusCode })!;
        Assert.Equal(expected, result);
    }

    private static (int statusCode, bool isChunked, long? contentLength) InvokeParseResponseHeaderInfo(byte[] bytes)
    {
        return ((int statusCode, bool isChunked, long? contentLength))
            s_parseResponseHeaderInfoMethod.Invoke(
                null,
                new object[] { new ReadOnlyMemory<byte>(bytes) })!;
    }
}
