namespace TinyProxy.Tests.Config;

public class ConfigParserReverseProxyTests
{
    [Fact]
    public void Parse_ReversePath_WithExplicitPath_NormalizesPathAndUsesTinyProxyOrder()
    {
        var config = ConfigParser.Parse("""
                                        ReversePath "/legacy" "http://legacy.example"
                                        ReversePath "/new/" "http://new.example"
                                        """);

        Assert.True(config.IsReverseProxyEnabled);
        Assert.Equal(2, config.ReversePaths.Count);

        Assert.Equal("/new/", config.ReversePaths[0].Path);
        Assert.Equal("http://new.example", config.ReversePaths[0].Url);

        Assert.Equal("/legacy/", config.ReversePaths[1].Path);
        Assert.Equal("http://legacy.example", config.ReversePaths[1].Url);
    }

    [Fact]
    public void Parse_ReversePath_WithOnlyUrl_UsesRootPath()
    {
        var config = ConfigParser.Parse("""
                                        ReversePath "http://backend.example:8080"
                                        """);

        var rule = Assert.Single(config.ReversePaths);
        Assert.Equal("/", rule.Path);
        Assert.Equal("http://backend.example:8080", rule.Url);
    }

    [Fact]
    public void Parse_ReversePath_InvalidRule_IsIgnored()
    {
        var config = ConfigParser.Parse("""
                                        ReversePath "api" "http://backend.example"
                                        ReversePath "/valid" "backend.example"
                                        """);

        Assert.Empty(config.ReversePaths);
        Assert.False(config.IsReverseProxyEnabled);
    }

    [Fact]
    public void Parse_ReverseDirectives_ParsesMagicAndBaseUrlAndLegacyAlias()
    {
        var config = ConfigParser.Parse("""
                                        ReverseMagic yes
                                        ReverseOnly on
                                        ReverseBaseURL "https://proxy.example"
                                        ReverseProxy /legacy http://legacy.example
                                        """);

        Assert.True(config.ReverseMagicEnabled);
        Assert.True(config.ReverseOnly);
        Assert.Equal("https://proxy.example", config.ReverseBaseUrl);
        var rule = Assert.Single(config.ReversePaths);
        Assert.Equal("/legacy/", rule.Path);
        Assert.Equal("http://legacy.example", rule.Url);
    }
}
