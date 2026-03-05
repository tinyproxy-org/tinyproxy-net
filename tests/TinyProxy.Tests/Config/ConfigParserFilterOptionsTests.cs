namespace TinyProxy.Tests.Config;

public class ConfigParserFilterOptionsTests
{
    [Fact]
    public void Parse_FilterDirective_LoadsPatternsFromFile()
    {
        var path = Path.GetTempFileName();

        try
        {
            File.WriteAllText(path, """
                                    # comment
                                    blocked\.example\.com

                                    denied\.example\.net
                                    """);

            var config = ConfigParser.Parse($"Filter {path}\n");

            Assert.Equal(path, config.FilterFile);
            Assert.Contains("blocked\\.example\\.com", config.FilterPatterns);
            Assert.Contains("denied\\.example\\.net", config.FilterPatterns);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_FilterDirective_StripsInlineComments_AndKeepsEscapedHash()
    {
        var path = Path.GetTempFileName();

        try
        {
            File.WriteAllText(path, """
                                    blocked\.example\.com   # trailing comment
                                    escaped\#hash\.example
                                      # full-line comment
                                    """);

            var config = ConfigParser.Parse($"Filter {path}\n");

            Assert.Contains("blocked\\.example\\.com", config.FilterPatterns);
            Assert.Contains("escaped\\#hash\\.example", config.FilterPatterns);
            Assert.DoesNotContain(config.FilterPatterns, pattern => pattern.Contains("trailing", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_FilterDirective_MissingFile_Throws()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"tinyproxy-filter-{Guid.NewGuid():N}.txt");

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigParser.Parse($"Filter {missingPath}\n"));

        Assert.Contains("Failed to load filter file", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("no", false)]
    public void Parse_FilterUrlsDirective_UsesTinyProxyBooleanSemantics(string value, bool expected)
    {
        var config = ConfigParser.Parse($"FilterURLs {value}\n");

        Assert.Equal(expected, config.FilterUrls);
    }

    [Fact]
    public void Parse_FilterTypeFnmatch_EnablesGlobMode()
    {
        var config = ConfigParser.Parse("FilterType fnmatch\n");

        Assert.True(config.FilterUseGlob);
    }

    [Fact]
    public void Parse_FilterUrlLegacyPattern_KeepsBackwardCompatibility()
    {
        var config = ConfigParser.Parse("FilterURL blocked\\.example\\.com\n");

        Assert.Contains("blocked\\.example\\.com", config.FilterPatterns);
    }
}
