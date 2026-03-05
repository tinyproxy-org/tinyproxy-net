namespace TinyProxy.Tests.Config;

public sealed class ConfigParserListenTests
{
    [Fact]
    public void Parse_Listen_DoesNotOverrideConfiguredPort()
    {
        var config = ConfigParser.Parse("""
                                        Port 3128
                                        Listen 0.0.0.0
                                        """);

        Assert.Equal("0.0.0.0", config.ListenAddress);
        Assert.Equal((ushort)3128, config.ListenPort);
    }

    [Fact]
    public void Parse_ListenWithIpv6Literal_DoesNotOverrideConfiguredPort()
    {
        var config = ConfigParser.Parse("""
                                        Listen ::1
                                        Port 8080
                                        """);

        Assert.Equal("::1", config.ListenAddress);
        Assert.Equal((ushort)8080, config.ListenPort);
    }

    [Fact]
    public void Parse_ListenWithHostPortToken_ThrowsFormatException()
    {
        var ex = Assert.Throws<FormatException>(() => ConfigParser.Parse("Listen 127.0.0.1:8888\n"));
        Assert.Contains("Listen", ex.Message, StringComparison.Ordinal);
    }
}
