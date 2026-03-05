namespace TinyProxy.Tests.Filter;

public class ConnectFilterTests
{
    [Fact]
    public void IsPortAllowed_AllowsAll_WhenNoConnectPortConfigured()
    {
        var config = new Configuration { AllowedConnectPorts = new HashSet<ushort>() };
        var filter = new ConnectFilter(config);

        Assert.True(filter.IsPortAllowed(443));
        Assert.True(filter.IsPortAllowed(8443));
        Assert.True(filter.IsPortAllowed(22));
    }

    [Fact]
    public void IsPortAllowed_OnlyAllowsConfiguredPorts_WhenConnectPortsConfigured()
    {
        var config = new Configuration { AllowedConnectPorts = new HashSet<ushort> { 563, 8443 } };
        var filter = new ConnectFilter(config);

        Assert.True(filter.IsPortAllowed(563));
        Assert.True(filter.IsPortAllowed(8443));
        Assert.False(filter.IsPortAllowed(443));
    }
}
