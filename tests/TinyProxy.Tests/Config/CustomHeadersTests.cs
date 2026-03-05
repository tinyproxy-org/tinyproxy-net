namespace TinyProxy.Tests.Config;

/// <summary>
/// Unit tests for custom headers (AddHeader directive).
/// Verifies header parsing and application.
/// </summary>
public class CustomHeadersTests
{
    [Fact]
    public void Configuration_DefaultsToEmptyCustomHeaders()
    {
        var config = new Configuration();

        Assert.Empty(config.CustomHeaders);
    }

    [Fact]
    public void Configuration_AcceptsCustomHeaders()
    {
        var headers = new List<HttpHeader>
        {
            new() { Name = "X-Custom-Header", Value = "CustomValue" },
            new() { Name = "X-Another-Header", Value = "AnotherValue" }
        };

        var config = new Configuration
        {
            CustomHeaders = headers
        };

        Assert.Equal(2, config.CustomHeaders.Count);
        Assert.Contains(config.CustomHeaders, h => h.Name == "X-Custom-Header");
    }

    [Fact]
    public void HttpHeader_Record_HoldsNameAndValue()
    {
        var header = new HttpHeader
        {
            Name = "X-Test",
            Value = "TestValue"
        };

        Assert.Equal("X-Test", header.Name);
        Assert.Equal("TestValue", header.Value);
    }

    [Fact]
    public void Parse_AddHeader_SingleHeader()
    {
        var content = "AddHeader \"X-Custom: CustomValue\"";
        var config = ConfigParser.Parse(content);

        Assert.Single(config.CustomHeaders);
        Assert.Equal("X-Custom", config.CustomHeaders[0].Name);
        Assert.Equal("CustomValue", config.CustomHeaders[0].Value);
    }

    [Fact]
    public void Parse_AddHeader_TinyProxyUpstreamSyntax_SingleHeader()
    {
        var content = "AddHeader \"X-Custom\" \"CustomValue\"";
        var config = ConfigParser.Parse(content);

        Assert.Single(config.CustomHeaders);
        Assert.Equal("X-Custom", config.CustomHeaders[0].Name);
        Assert.Equal("CustomValue", config.CustomHeaders[0].Value);
    }

    [Fact]
    public void Parse_AddHeader_MultipleHeaders()
    {
        var content = @"
AddHeader ""X-Header-One"" ""Value-One""
AddHeader ""X-Header-Two"" ""Value-Two""
AddHeader ""X-Header-Three"" ""Value-Three""
";
        var config = ConfigParser.Parse(content);

        Assert.Equal(3, config.CustomHeaders.Count);
        Assert.Contains(config.CustomHeaders, h => h.Name == "X-Header-One" && h.Value == "Value-One");
        Assert.Contains(config.CustomHeaders, h => h.Name == "X-Header-Two" && h.Value == "Value-Two");
        Assert.Contains(config.CustomHeaders, h => h.Name == "X-Header-Three" && h.Value == "Value-Three");
    }

    [Fact]
    public void Parse_AddHeader_TinyProxyUpstreamSyntax_ValueWithSpaces()
    {
        var content = "AddHeader \"X-Proxy-Note\" \"value with spaces\"";
        var config = ConfigParser.Parse(content);

        Assert.Single(config.CustomHeaders);
        Assert.Equal("X-Proxy-Note", config.CustomHeaders[0].Name);
        Assert.Equal("value with spaces", config.CustomHeaders[0].Value);
    }

    [Fact]
    public void Parse_AddHeader_WithSpacesAroundColon()
    {
        var content = "AddHeader \"X-Test :  ValueWithSpaces  \"";
        var config = ConfigParser.Parse(content);

        Assert.Single(config.CustomHeaders);
        Assert.Equal("X-Test", config.CustomHeaders[0].Name);
        Assert.Equal("ValueWithSpaces", config.CustomHeaders[0].Value);
    }

    [Fact]
    public void Parse_AddHeader_WithSpecialCharacters()
    {
        var content = "AddHeader \"X-Special: test-value-123_456\"";
        var config = ConfigParser.Parse(content);

        Assert.Single(config.CustomHeaders);
        Assert.Equal("X-Special", config.CustomHeaders[0].Name);
        Assert.Equal("test-value-123_456", config.CustomHeaders[0].Value);
    }

    [Fact]
    public void Parse_AddHeader_WithMultipleColons_ValueContainsColon()
    {
        var content = "AddHeader \"X-Time: 10:30\"";
        var config = ConfigParser.Parse(content);

        Assert.Single(config.CustomHeaders);
        // Only first colon should be used for splitting
        Assert.Equal("X-Time", config.CustomHeaders[0].Name);
        Assert.Equal("10:30", config.CustomHeaders[0].Value);
    }

    [Fact]
    public void Parse_AddHeader_EmptyHeader_DoesNotAdd()
    {
        var content = "AddHeader \":\"";
        var config = ConfigParser.Parse(content);

        // Empty header name should not be added
        // The colon index would be 0, and name would be empty
        Assert.Empty(config.CustomHeaders);
    }

    [Fact]
    public void Parse_AddHeader_CombinedWithOtherDirectives()
    {
        var content = @"
Listen 127.0.0.1
Port 8888
AddHeader ""X-Proxy-Auth"" ""secret123""
AddHeader ""X-Forwarded-By"" ""my-proxy""
MaxClients 100
";
        var config = ConfigParser.Parse(content);

        Assert.Equal(2, config.CustomHeaders.Count);
        Assert.Equal("127.0.0.1", config.ListenAddress);
        Assert.Equal((ushort)8888, config.ListenPort);
        Assert.Equal(100, config.MaxClients);
    }
}
