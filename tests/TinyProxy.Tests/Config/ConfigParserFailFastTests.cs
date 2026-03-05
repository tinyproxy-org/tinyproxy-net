namespace TinyProxy.Tests.Config;

public sealed class ConfigParserFailFastTests
{
    [Fact]
    public void Parse_UnknownDirective_ThrowsFormatExceptionWithLineNumber()
    {
        var ex = Assert.Throws<FormatException>(() => ConfigParser.Parse("""
                                                                     Port 8888
                                                                     UnknownDirective yes
                                                                     """));

        Assert.Contains("line 2", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UnknownDirective", ex.Message);
    }

    [Fact]
    public void Parse_UnparseableLine_ThrowsFormatExceptionWithLineNumber()
    {
        var ex = Assert.Throws<FormatException>(() => ConfigParser.Parse("""
                                                                     Port 8888
                                                                     not-a-valid-directive
                                                                     """));

        Assert.Contains("line 2", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not-a-valid-directive", ex.Message);
    }

    [Fact]
    public void Parse_FilterDirective_InvalidRegex_ThrowsFormatException()
    {
        var path = Path.GetTempFileName();

        try
        {
            File.WriteAllText(path, "[invalid-regex\n");

            var ex = Assert.Throws<FormatException>(() => ConfigParser.Parse($"""
                Port 8888
                Filter {path}
                """));

            Assert.Contains("Invalid filter regex", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("Port abc", "Port")]
    [InlineData("Port 70000", "Port")]
    [InlineData("Timeout nope", "Timeout")]
    [InlineData("ConnectPort invalid", "ConnectPort")]
    [InlineData("FilterType wildcard", "FilterType")]
    public void Parse_InvalidDirectiveValue_ThrowsFormatException(string line, string directive)
    {
        var ex = Assert.Throws<FormatException>(() => ConfigParser.Parse($"""
            Port 8888
            {line}
            """));

        Assert.Contains(directive, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_TimeoutZero_FallsBackToDefaultTimeout()
    {
        var config = ConfigParser.Parse("""
            Port 8888
            Timeout 0
            """);

        Assert.Equal(
            TimeSpan.FromSeconds(ProxyConstants.DefaultConnectionTimeoutSeconds),
            config.Timeout);
    }

    [Fact]
    public void Startup_LoadConfiguration_WhenFileMissing_ThrowsFileNotFound()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"tinyproxy-missing-{Guid.NewGuid():N}.conf");
        var loadConfiguration = GetLoadConfigurationMethod();

        var ex = Assert.Throws<TargetInvocationException>(() => loadConfiguration.Invoke(null, new object[] { missingPath }));
        Assert.IsType<FileNotFoundException>(ex.InnerException);
    }

    [Fact]
    public void Startup_TryLoadConfiguration_WhenFileMissing_ReturnsFalseAndWritesToStderr()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"tinyproxy-missing-{Guid.NewGuid():N}.conf");
        var tryLoadConfiguration = GetTryLoadConfigurationMethod();
        using var stderr = new StringWriter();
        var parameters = new object?[] { missingPath, stderr, null };

        var result = tryLoadConfiguration.Invoke(null, parameters);

        Assert.False(Assert.IsType<bool>(result));
        Assert.Null(parameters[2]);

        var errorMessage = stderr.ToString();
        Assert.Contains("Could not open config file", errorMessage, StringComparison.Ordinal);
        Assert.Contains("Usage: tinyproxy [-c <config-file>]", errorMessage, StringComparison.Ordinal);
    }

    private static MethodInfo GetLoadConfigurationMethod()
    {
        var programType = typeof(Configuration).Assembly.GetType("TinyProxy.Program", throwOnError: true);
        Assert.NotNull(programType);

        var method = programType!.GetMethod("LoadConfiguration", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!;
    }

    private static MethodInfo GetTryLoadConfigurationMethod()
    {
        var programType = typeof(Configuration).Assembly.GetType("TinyProxy.Program", throwOnError: true);
        Assert.NotNull(programType);

        var method = programType!.GetMethod("TryLoadConfiguration", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!;
    }
}
