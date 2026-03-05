namespace TinyProxy.Tests.Filter;

public class AnonymousFilterTests
{
    [Fact]
    public void Constructor_WithConfiguredAnonymousHeaders_AddsImplicitContentHeaders()
    {
        var filter = new AnonymousFilter(new[] { "User-Agent" });

        Assert.True(filter.IsHeaderAllowed("User-Agent"));
        Assert.True(filter.IsHeaderAllowed("Content-Length"));
        Assert.True(filter.IsHeaderAllowed("Content-Type"));
    }

    [Fact]
    public void Constructor_WithEmptyAnonymousHeaders_DoesNotEnableImplicitContentHeaders()
    {
        var filter = new AnonymousFilter(Array.Empty<string>());

        Assert.False(filter.IsHeaderAllowed("Content-Length"));
        Assert.False(filter.IsHeaderAllowed("Content-Type"));
    }
}
