namespace TinyProxy.Tests.Config;

public class ConfigParserUpstreamTests
{
    [Fact]
    public void Parse_Upstream_DefaultRule_UsesTinyProxyUpstreamSyntax()
    {
        var config = ConfigParser.Parse("Upstream http proxy.example:3128\n");

        Assert.NotNull(config.UpstreamProxy);
        Assert.Equal("proxy.example", config.UpstreamProxy!.Host);
        Assert.Equal((ushort)3128, config.UpstreamProxy.Port);
        Assert.Equal(UpstreamProxyType.Http, config.UpstreamProxy.Type);

        var rule = Assert.Single(config.UpstreamProxyRules);
        Assert.Null(rule.Domain);
        Assert.NotNull(rule.Proxy);
        Assert.Equal("proxy.example", rule.Proxy!.Host);
    }

    [Fact]
    public void Parse_Upstream_NoneRule_AddsBypassRule()
    {
        var config = ConfigParser.Parse("Upstream none .internal.example\n");

        Assert.Null(config.UpstreamProxy);
        var rule = Assert.Single(config.UpstreamProxyRules);
        Assert.Equal(".internal.example", rule.Domain);
        Assert.Null(rule.Proxy);
    }

    [Fact]
    public void Parse_Upstream_ParsesCredentialsIpv6AndDomain()
    {
        var config = ConfigParser.Parse("Upstream socks5 alice:secret@[2001:db8::1]:1080 .corp.example\n");

        var rule = Assert.Single(config.UpstreamProxyRules);
        Assert.Equal(".corp.example", rule.Domain);
        Assert.NotNull(rule.Proxy);
        Assert.Equal(UpstreamProxyType.Socks5, rule.Proxy!.Type);
        Assert.Equal("alice", rule.Proxy.Username);
        Assert.Equal("secret", rule.Proxy.Password);
        Assert.Equal("2001:db8::1", rule.Proxy.Host);
        Assert.Equal((ushort)1080, rule.Proxy.Port);
    }

    [Fact]
    public void Parse_Upstream_DuplicateDefaultRule_KeepsFirstLikeTinyproxyUpstream()
    {
        var config = ConfigParser.Parse("""
                                        Upstream http first.proxy:3128
                                        Upstream http second.proxy:8080
                                        """);

        Assert.NotNull(config.UpstreamProxy);
        Assert.Equal("first.proxy", config.UpstreamProxy!.Host);
        var defaultRules = config.UpstreamProxyRules.Where(r => r.Domain == null).ToList();
        Assert.Single(defaultRules);
        Assert.Equal("first.proxy", defaultRules[0].Proxy!.Host);
    }

    [Fact]
    public void Parse_Upstream_LegacySchemeSyntax_RemainsSupported()
    {
        var config = ConfigParser.Parse("Upstream socks4://legacy.proxy:1080\n");

        Assert.NotNull(config.UpstreamProxy);
        Assert.Equal("legacy.proxy", config.UpstreamProxy!.Host);
        Assert.Equal(UpstreamProxyType.Socks4, config.UpstreamProxy.Type);
        Assert.Equal((ushort)1080, config.UpstreamProxy.Port);
    }

    [Fact]
    public void ResolveUpstreamProxy_UsesDomainRulesAndBypassBeforeDefault()
    {
        var config = ConfigParser.Parse("""
                                        Upstream http default.proxy:3128
                                        Upstream socks5 socks.proxy:1080 .corp.example
                                        Upstream none .bypass.example
                                        """);

        var bypass = config.ResolveUpstreamProxy("svc.bypass.example");
        Assert.Null(bypass);

        var corp = config.ResolveUpstreamProxy("api.corp.example");
        Assert.NotNull(corp);
        Assert.Equal(UpstreamProxyType.Socks5, corp!.Type);
        Assert.Equal("socks.proxy", corp.Host);

        var internet = config.ResolveUpstreamProxy("www.example.org");
        Assert.NotNull(internet);
        Assert.Equal(UpstreamProxyType.Http, internet!.Type);
        Assert.Equal("default.proxy", internet.Host);
    }

    [Fact]
    public void ResolveUpstreamProxy_DomainPrecedence_MatchesTinyProxyListOrder()
    {
        var config = ConfigParser.Parse("""
                                        Upstream http old.proxy:3128 .example.com
                                        Upstream socks5 new.proxy:1080 .example.com
                                        """);

        var resolved = config.ResolveUpstreamProxy("api.example.com");

        Assert.NotNull(resolved);
        Assert.Equal("new.proxy", resolved!.Host);
        Assert.Equal(UpstreamProxyType.Socks5, resolved.Type);
    }

    [Fact]
    public void ResolveUpstreamProxy_SupportsCidrAndDottedMaskRules()
    {
        var config = ConfigParser.Parse("""
                                        Upstream http cidr.proxy:3128 10.0.0.0/8
                                        Upstream http mask.proxy:3129 192.168.10.0/255.255.255.0
                                        """);

        var cidr = config.ResolveUpstreamProxy("10.7.8.9");
        Assert.NotNull(cidr);
        Assert.Equal("cidr.proxy", cidr!.Host);

        var mask = config.ResolveUpstreamProxy("192.168.10.45");
        Assert.NotNull(mask);
        Assert.Equal("mask.proxy", mask!.Host);

        var miss = config.ResolveUpstreamProxy("203.0.113.10");
        Assert.Null(miss);
    }
}
