namespace TinyProxy.Tests.Protocol;

public class HttpForwarderRequestTargetTests
{
    private static readonly MethodInfo s_getForwardRequestTargetMethod =
        typeof(HttpForwarder).GetMethod(
            "GetForwardRequestTarget",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void GetForwardRequestTarget_UsesOriginForm_ForDirectConnections()
    {
        var result = InvokeGetForwardRequestTarget(
            "http://example.com:8080/path/to?a=1&b=2",
            "example.com",
            8080,
            useAbsoluteUri: false);

        Assert.Equal("/path/to?a=1&b=2", result);
    }

    [Fact]
    public void GetForwardRequestTarget_UsesAbsoluteForm_ForHttpUpstream()
    {
        var result = InvokeGetForwardRequestTarget(
            "/path/to?a=1",
            "example.com",
            8080,
            useAbsoluteUri: true);

        Assert.Equal("http://example.com:8080/path/to?a=1", result);
    }

    [Fact]
    public void GetForwardRequestTarget_KeepsAsteriskForm()
    {
        var result = InvokeGetForwardRequestTarget(
            "*",
            "example.com",
            80,
            useAbsoluteUri: false);

        Assert.Equal("*", result);
    }

    [Fact]
    public void GetForwardRequestTarget_RewritesNonHttpAbsoluteUri_ForHttpUpstream()
    {
        var result = InvokeGetForwardRequestTarget(
            "ftp://ftp.example.com/pub/file.txt",
            "ftp.example.com",
            80,
            useAbsoluteUri: true);

        Assert.Equal("http://ftp.example.com:80/pub/file.txt", result);
    }

    [Fact]
    public void GetForwardRequestTarget_StripsUserInfo_ForHttpAbsoluteUri()
    {
        var result = InvokeGetForwardRequestTarget(
            "http://alice:secret@example.com:8080/private?a=1",
            "example.com",
            8080,
            useAbsoluteUri: true);

        Assert.Equal("http://example.com:8080/private?a=1", result);
    }

    private static string InvokeGetForwardRequestTarget(string uri, string host, int port, bool useAbsoluteUri)
    {
        return (string)s_getForwardRequestTargetMethod.Invoke(
            null,
            new object[] { uri, host, port, useAbsoluteUri })!;
    }
}
