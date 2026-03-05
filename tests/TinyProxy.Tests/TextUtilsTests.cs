namespace TinyProxy.Tests;

/// <summary>
/// Tests for TextUtils.
/// </summary>
public class TextUtilsTests
{
    [Fact]
    public void Chomp_EmptyBuffer_ReturnsZero()
    {
        byte[] buffer = [];
        int result = TextUtils.Chomp(buffer, 0);
        Assert.Equal(0, result);
    }

    [Fact]
    public void Chomp_LineEndingOnly_ReturnsCount()
    {
        byte[] buffer = "test\r\n"u8.ToArray();
        int result = TextUtils.Chomp(buffer, buffer.Length);
        Assert.Equal(2, result);
    }

    [Fact]
    public void Chomp_MultipleLineEndings_ReturnsCount()
    {
        byte[] buffer = "test\r\n\r\n"u8.ToArray();
        int result = TextUtils.Chomp(buffer, buffer.Length);
        Assert.Equal(4, result);
    }

    [Fact]
    public void Chomp_NoLineEndings_ReturnsZero()
    {
        byte[] buffer = "test"u8.ToArray();
        int result = TextUtils.Chomp(buffer, buffer.Length);
        Assert.Equal(0, result);
    }

    [Fact]
    public void Chomp_StringBuilder_RemovesLineEndings()
    {
        var sb = new StringBuilder("test\r\n");
        TextUtils.Chomp(sb);
        Assert.Equal("test", sb.ToString());
    }

    [Fact]
    public void Strlcpy_SourceShorterThanDest_CopiesAndNullTerminates()
    {
        char[] dst = new char[256];
        string src = "hello";
        int result = TextUtils.Strlcpy(dst, src, dst.Length);

        Assert.Equal(src.Length, result);
        Assert.Equal(src, new string(dst).TrimEnd('\0'));
        Assert.Equal('\0', dst[src.Length]);
    }

    [Fact]
    public void Strlcpy_SourceLongerThanDest_TruncatesAndNullTerminates()
    {
        char[] dst = new char[5];
        string src = "hello world";
        int result = TextUtils.Strlcpy(dst, src, dst.Length);

        Assert.Equal(src.Length, result);
        Assert.Equal("hell", new string(dst).TrimEnd('\0'));
        Assert.Equal('\0', dst[4]);
    }

    [Fact]
    public void Strlcpy_ZeroSize_ReturnsSourceLength()
    {
        char[] dst = [];
        string src = "hello";
        int result = TextUtils.Strlcpy(dst, src, 0);

        Assert.Equal(src.Length, result);
    }

    [Fact]
    public void IndexOfIgnoreCase_CaseInsensitiveMatch_ReturnsIndex()
    {
        byte[] span = "Hello World"u8.ToArray();
        byte[] value = "WORLD"u8.ToArray();
        int result = TextUtils.IndexOfIgnoreCase(span, value);

        Assert.Equal(6, result);
    }

    [Fact]
    public void IndexOfIgnoreCase_NoMatch_ReturnsMinusOne()
    {
        byte[] span = "Hello World"u8.ToArray();
        byte[] value = "xyz"u8.ToArray();
        int result = TextUtils.IndexOfIgnoreCase(span, value);

        Assert.Equal(-1, result);
    }

    [Fact]
    public void Trim_RemovesWhitespace()
    {
        byte[] span = "  hello world  \r\n"u8.ToArray();
        var result = TextUtils.Trim(span);

        Assert.Equal("hello world"u8.ToArray(), result.ToArray());
    }

    [Fact]
    public void TryParseHostPort_StripsUserInfo_AndParsesPort()
    {
        var ok = TextUtils.TryParseHostPort("user:pass@example.com:8443", 80, out var host, out var port);

        Assert.True(ok);
        Assert.Equal("example.com", host);
        Assert.Equal(8443, port);
    }

    [Fact]
    public void TryParseHostPort_StripsUserInfo_ForIpv6Literal()
    {
        var ok = TextUtils.TryParseHostPort("user@[2001:db8::1]:9443", 80, out var host, out var port);

        Assert.True(ok);
        Assert.Equal("2001:db8::1", host);
        Assert.Equal(9443, port);
    }

    [Fact]
    public void TryParseHostPort_StripsFromFirstAt_LikeTinyproxyUpstream()
    {
        var ok = TextUtils.TryParseHostPort("user@realm@example.com:8443", 80, out var host, out var port);

        Assert.True(ok);
        Assert.Equal("realm@example.com", host);
        Assert.Equal(8443, port);
    }

    [Fact]
    public void TryParseHostPort_InvalidPortToken_StripsPortAndUsesDefault()
    {
        var ok = TextUtils.TryParseHostPort("example.com:notaport", 80, out var host, out var port);

        Assert.True(ok);
        Assert.Equal("example.com", host);
        Assert.Equal(80, port);
    }

    [Fact]
    public void TryParseHostPort_EmptyPortToken_StripsColonAndUsesDefault()
    {
        var ok = TextUtils.TryParseHostPort("example.com:", 443, out var host, out var port);

        Assert.True(ok);
        Assert.Equal("example.com", host);
        Assert.Equal(443, port);
    }

    [Fact]
    public void TryParseHostPort_UnbracketedIpv6Literal_UsesWholeHostAndDefaultPort()
    {
        var ok = TextUtils.TryParseHostPort("2001:db8::1", 80, out var host, out var port);

        Assert.True(ok);
        Assert.Equal("2001:db8::1", host);
        Assert.Equal(80, port);
    }

    [Fact]
    public void TryParseHostPort_UnbracketedIpv6WithUserInfo_UsesWholeHostAndDefaultPort()
    {
        var ok = TextUtils.TryParseHostPort("user@2001:db8::1", 443, out var host, out var port);

        Assert.True(ok);
        Assert.Equal("2001:db8::1", host);
        Assert.Equal(443, port);
    }

    [Fact]
    public void TryParseHostPort_BracketedIpv6WithTrailingGarbage_ReturnsFalse()
    {
        var ok = TextUtils.TryParseHostPort("[2001:db8::1]oops", 443, out var host, out var port);

        Assert.False(ok);
        Assert.Equal(string.Empty, host);
        Assert.Equal(443, port);
    }

    [Fact]
    public void TryParseHostPort_MultiColonNonIpv6Host_ReturnsFalse()
    {
        var ok = TextUtils.TryParseHostPort("example.com:80:90", 443, out var host, out var port);

        Assert.False(ok);
        Assert.Equal(string.Empty, host);
        Assert.Equal(443, port);
    }
}
