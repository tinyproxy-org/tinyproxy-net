namespace TinyProxy.Tests.Config;

public class ConfigParserConnectPortTests
{
    [Fact]
    public void Parse_LeavesConnectPortListEmpty_WhenDirectiveMissing()
    {
        var config = ConfigParser.Parse("Port 8888\n");

        Assert.Empty(config.AllowedConnectPorts);
    }

    [Fact]
    public void Parse_UsesOnlyConfiguredConnectPorts_WithoutImplicit443()
    {
        var content = """
                      ConnectPort 563
                      ConnectPort 8443
                      """;

        var config = ConfigParser.Parse(content);

        Assert.Equal(2, config.AllowedConnectPorts.Count);
        Assert.Contains((ushort)563, config.AllowedConnectPorts);
        Assert.Contains((ushort)8443, config.AllowedConnectPorts);
        Assert.DoesNotContain((ushort)443, config.AllowedConnectPorts);
    }
}
