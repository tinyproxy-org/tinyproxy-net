using HttpMethod = TinyProxy.Protocol.Http.HttpMethod;

namespace TinyProxy.Tests.Filter;

public class UrlFilterTests
{
    [Fact]
    public void IsAllowed_DefaultDenyWithoutRules_ReturnsFalse()
    {
        var config = new Configuration
        {
            FilterDefaultDeny = true
        };

        var filter = new UrlFilter(config);

        Assert.False(filter.IsAllowed("allowed.example.com"));
    }

    [Fact]
    public void IsRequestAllowed_FiltersByHostByDefault()
    {
        var config = new Configuration
        {
            FilterPatterns = new List<string> { "blocked\\.example\\.com" }
        };

        var filter = new UrlFilter(config);
        var request = new HttpRequest
        {
            Method = HttpMethod.Get,
            Uri = "/index.html",
            Host = "blocked.example.com"
        };

        Assert.False(filter.IsRequestAllowed(request));
    }

    [Fact]
    public void IsRequestAllowed_DoesNotFilterByPath_WhenFilterUrlsDisabled()
    {
        var config = new Configuration
        {
            FilterPatterns = new List<string> { "forbidden-path" },
            FilterUrls = false
        };

        var filter = new UrlFilter(config);
        var request = new HttpRequest
        {
            Method = HttpMethod.Get,
            Uri = "/forbidden-path",
            Host = "allowed.example.com"
        };

        Assert.True(filter.IsRequestAllowed(request));
    }

    [Fact]
    public void IsRequestAllowed_FiltersByFullUrl_WhenFilterUrlsEnabled()
    {
        var config = new Configuration
        {
            FilterPatterns = new List<string> { "forbidden-path" },
            FilterUrls = true
        };

        var filter = new UrlFilter(config);
        var request = new HttpRequest
        {
            Method = HttpMethod.Get,
            Uri = "/forbidden-path",
            Host = "allowed.example.com"
        };

        Assert.False(filter.IsRequestAllowed(request));
    }

    [Fact]
    public void IsRequestAllowed_FilterUrlsEnabled_UsesRawConnectTargetLikeTinyproxyUpstream()
    {
        var config = new Configuration
        {
            FilterPatterns = new List<string> { "^blocked\\.example\\.com:8443$" },
            FilterUrls = true
        };

        var filter = new UrlFilter(config);
        var request = new HttpRequest
        {
            Method = HttpMethod.Connect,
            Uri = "blocked.example.com:8443",
            Host = "blocked.example.com:8443"
        };

        Assert.False(filter.IsRequestAllowed(request));
    }

    [Fact]
    public void IsRequestAllowed_UsesGlobRules_WhenFilterTypeIsFnmatch()
    {
        var config = new Configuration
        {
            FilterPatterns = new List<string> { "*.example.com" },
            FilterUseGlob = true
        };

        var filter = new UrlFilter(config);
        var request = new HttpRequest
        {
            Method = HttpMethod.Get,
            Uri = "/",
            Host = "foo.example.com"
        };

        Assert.False(filter.IsRequestAllowed(request));
    }

    [Fact]
    public void IsAllowed_GlobCharacterClass_MatchesLikeFnmatch()
    {
        var config = new Configuration
        {
            FilterPatterns = new List<string> { "img[0-9].example.com" },
            FilterUseGlob = true
        };

        var filter = new UrlFilter(config);

        Assert.False(filter.IsAllowed("img3.example.com"));
        Assert.True(filter.IsAllowed("imgx.example.com"));
    }

    [Fact]
    public void IsAllowed_GlobNegatedClass_MatchesLikeFnmatch()
    {
        var config = new Configuration
        {
            FilterPatterns = new List<string> { "img[!0-9].example.com" },
            FilterUseGlob = true
        };

        var filter = new UrlFilter(config);

        Assert.False(filter.IsAllowed("imgx.example.com"));
        Assert.True(filter.IsAllowed("img3.example.com"));
    }

    [Fact]
    public void IsAllowed_GlobBackslashEscape_MatchesLiteralWildcard()
    {
        var config = new Configuration
        {
            FilterPatterns = new List<string> { @"file\*.txt" },
            FilterUseGlob = true
        };

        var filter = new UrlFilter(config);

        Assert.False(filter.IsAllowed("file*.txt"));
        Assert.True(filter.IsAllowed("file123.txt"));
    }

    [Fact]
    public void Constructor_InvalidRegexPattern_Throws()
    {
        var config = new Configuration
        {
            FilterPatterns = new List<string> { "[invalid-regex" }
        };

        Assert.Throws<InvalidOperationException>(() => new UrlFilter(config));
    }
}
