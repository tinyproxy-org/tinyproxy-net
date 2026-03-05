namespace TinyProxy.Tests.Config;

public class ConfigParserBooleanTests
{
    [Theory]
    [InlineData("yes", true)]
    [InlineData("no", false)]
    [InlineData("on", true)]
    [InlineData("off", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Parse_BooleanDirectives_SupportsTinyProxySemantics(string boolValue, bool expected)
    {
        var content = $"""
                       Syslog {boolValue}
                       ViaHeader {boolValue}
                       XTinyproxy {boolValue}
                       FilterDefaultDeny {boolValue}
                       FilterCaseSensitive {boolValue}
                       BindSame {boolValue}
                       Transparent {boolValue}
                       Verbose {boolValue}
                       """;

        var config = ConfigParser.Parse(content);

        Assert.Equal(expected, config.UseSyslog);
        Assert.Equal(expected, config.AddViaHeader);
        Assert.Equal(expected, config.AddXTinyproxyHeader);
        Assert.Equal(expected, config.FilterDefaultDeny);
        Assert.Equal(expected, config.FilterCaseSensitive);
        Assert.Equal(expected, config.BindSame);
        Assert.Equal(expected, config.IsTransparentProxyEnabled);
        Assert.Equal(expected, config.Verbose);
    }

    [Fact]
    public void Parse_InvalidBooleanValue_ThrowsFormatException()
    {
        var content = """
                      Syslog maybe
                      ViaHeader maybe
                      XTinyproxy maybe
                      """;

        var ex = Assert.Throws<FormatException>(() => ConfigParser.Parse(content));

        Assert.Contains("Syslog", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("yes", false)]
    [InlineData("on", false)]
    [InlineData("no", true)]
    [InlineData("off", true)]
    public void Parse_DisableViaHeader_UsesTinyProxyUpstreamSemantics(string directiveValue, bool expectedAddViaHeader)
    {
        var config = ConfigParser.Parse($"DisableViaHeader {directiveValue}");

        Assert.Equal(expectedAddViaHeader, config.AddViaHeader);
    }
}
