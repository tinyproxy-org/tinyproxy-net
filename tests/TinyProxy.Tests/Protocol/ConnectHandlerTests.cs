namespace TinyProxy.Tests.Protocol;

public class ConnectHandlerTests
{
    private static readonly MethodInfo s_getEstablishedResponseMethod =
        typeof(ConnectHandler).GetMethod(
            "GetEstablishedResponse",
            BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo s_tryParseConnectTargetMethod =
        typeof(ConnectHandler).GetMethod(
            "TryParseConnectTarget",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    [Theory]
    [InlineData("HTTP/1.0", "HTTP/1.0 200 Connection established\r\nProxy-agent: TinyProxy.NET\r\n\r\n")]
    [InlineData("HTTP/1.1", "HTTP/1.1 200 Connection established\r\nProxy-agent: TinyProxy.NET\r\n\r\n")]
    [InlineData("HTTP/1.2", "HTTP/1.2 200 Connection established\r\nProxy-agent: TinyProxy.NET\r\n\r\n")]
    [InlineData("HTTP/1.1beta", "HTTP/1.1 200 Connection established\r\nProxy-agent: TinyProxy.NET\r\n\r\n")]
    [InlineData("HTTP/2.0", "HTTP/1.0 200 Connection established\r\nProxy-agent: TinyProxy.NET\r\n\r\n")]
    [InlineData("HTTP/2.0beta", "HTTP/1.0 200 Connection established\r\nProxy-agent: TinyProxy.NET\r\n\r\n")]
    public void GetEstablishedResponse_AlignsWithTinyproxyHttpVersionBehavior(string requestVersion, string expectedResponseLine)
    {
        var response = (ReadOnlyMemory<byte>)s_getEstablishedResponseMethod.Invoke(
            null,
            new object?[] { requestVersion })!;

        var text = Encoding.ASCII.GetString(response.Span);
        Assert.Equal(expectedResponseLine, text);
    }

    [Theory]
    [InlineData("example.com:notaport")]
    [InlineData("example.com:70000")]
    [InlineData("example.com:")]
    [InlineData("[::1]:70000")]
    public void TryParseConnectTarget_WithInvalidExplicitPort_ReturnsFalse(string uri)
    {
        var args = new object?[] { uri, null, 0 };
        var ok = (bool)s_tryParseConnectTargetMethod.Invoke(null, args)!;

        Assert.False(ok);
    }

    [Fact]
    public void TryParseConnectTarget_WithoutExplicitPort_UsesDefault443()
    {
        var args = new object?[] { "example.com", null, 0 };
        var ok = (bool)s_tryParseConnectTargetMethod.Invoke(null, args)!;

        Assert.True(ok);
        Assert.Equal("example.com", Assert.IsType<string>(args[1]));
        Assert.Equal(443, Assert.IsType<int>(args[2]));
    }
}
