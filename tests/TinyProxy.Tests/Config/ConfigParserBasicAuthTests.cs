namespace TinyProxy.Tests.Config;

public class ConfigParserBasicAuthTests
{
    [Fact]
    public void Parse_BasicAuth_SupportsTinyProxySpaceSeparatedSyntax()
    {
        var config = ConfigParser.Parse("BasicAuth alice s3cret\n");

        Assert.NotNull(config.BasicAuth);
        Assert.Equal("alice", config.BasicAuth!.Username);
        Assert.Equal("s3cret", config.BasicAuth.Password);
    }

    [Fact]
    public void Parse_BasicAuth_KeepsBackwardCompatibilityWithColonSyntax()
    {
        var config = ConfigParser.Parse("BasicAuth bob:p@ss\n");

        Assert.NotNull(config.BasicAuth);
        Assert.Equal("bob", config.BasicAuth!.Username);
        Assert.Equal("p@ss", config.BasicAuth.Password);
    }

    [Fact]
    public void Parse_BasicAuth_SupportsQuotedValues()
    {
        var config = ConfigParser.Parse("BasicAuth \"alice\" \"my pass\"\n");

        Assert.NotNull(config.BasicAuth);
        Assert.Equal("alice", config.BasicAuth!.Username);
        Assert.Equal("my pass", config.BasicAuth.Password);
    }

    [Fact]
    public void Parse_BasicAuth_SupportsMultipleDirectives()
    {
        var config = ConfigParser.Parse("""
                                        BasicAuth alice a1
                                        BasicAuth bob b2
                                        """);

        Assert.NotNull(config.BasicAuth);
        Assert.Equal("alice", config.BasicAuth!.Username);
        Assert.Equal(2, config.BasicAuthUsers.Count);
        Assert.Contains(config.BasicAuthUsers, u => u.Username == "alice" && u.Password == "a1");
        Assert.Contains(config.BasicAuthUsers, u => u.Username == "bob" && u.Password == "b2");
    }
}
