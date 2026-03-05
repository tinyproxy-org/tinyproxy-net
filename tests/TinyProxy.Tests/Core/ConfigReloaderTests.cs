namespace TinyProxy.Tests.Core;

public sealed class ConfigReloaderTests : IDisposable
{
    private readonly string _tempDirectory;

    public ConfigReloaderTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"tinyproxy-reloader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void ReloadConfig_WhenAclChanges_ReloadsEvenIfCoreScalarsStayTheSame()
    {
        var configPath = Path.Combine(_tempDirectory, "tinyproxy.conf");
        File.WriteAllText(configPath, """
                                      Port 8888
                                      Allow 10.0.0.1
                                      """);

        var snapshots = new List<Configuration>();
        using var reloader = new ConfigReloader(
            new NullLogger(),
            configPath,
            config => snapshots.Add(config),
            enableFileWatcher: false);

        reloader.ReloadConfig();

        File.WriteAllText(configPath, """
                                      Port 8888
                                      Allow 10.0.0.2
                                      """);

        reloader.ReloadConfig();

        Assert.Equal(2, snapshots.Count);
        Assert.Contains("10.0.0.1", snapshots[0].AllowIPs);
        Assert.Contains("10.0.0.2", snapshots[1].AllowIPs);
    }

    [Fact]
    public void ReloadConfig_WhenContentIsUnchanged_SkipsRedundantReload()
    {
        var configPath = Path.Combine(_tempDirectory, "tinyproxy.conf");
        File.WriteAllText(configPath, "Port 8888\n");

        var reloadCount = 0;
        using var reloader = new ConfigReloader(
            new NullLogger(),
            configPath,
            _ => reloadCount++,
            enableFileWatcher: false);

        reloader.ReloadConfig();
        reloader.ReloadConfig();

        Assert.Equal(1, reloadCount);
    }

    [Fact]
    public void ReloadConfig_WhenFileBecomesMissing_KeepsLastKnownGood()
    {
        var configPath = Path.Combine(_tempDirectory, "tinyproxy.conf");
        File.WriteAllText(configPath, "Port 8888\n");

        var snapshots = new List<Configuration>();

        using var reloader = new ConfigReloader(
            new NullLogger(),
            configPath,
            config => snapshots.Add(config),
            enableFileWatcher: false);

        reloader.ReloadConfig();
        File.Delete(configPath);
        reloader.ReloadConfig();

        var config = Assert.Single(snapshots);
        Assert.Equal((ushort)8888, config.ListenPort);
    }

    [Fact]
    public void ReloadConfig_WhenParsingFails_KeepsLastKnownGood()
    {
        var configPath = Path.Combine(_tempDirectory, "tinyproxy.conf");
        File.WriteAllText(configPath, "Port 8888\n");

        var snapshots = new List<Configuration>();
        using var reloader = new ConfigReloader(
            new NullLogger(),
            configPath,
            config => snapshots.Add(config),
            enableFileWatcher: false);

        reloader.ReloadConfig();

        File.WriteAllText(configPath, "UnknownDirective value\n");
        reloader.ReloadConfig();

        var config = Assert.Single(snapshots);
        Assert.Equal((ushort)8888, config.ListenPort);
    }

    [Fact]
    public void ReloadConfig_WhenFilterRegexBecomesInvalid_KeepsLastKnownGood()
    {
        var configPath = Path.Combine(_tempDirectory, "tinyproxy.conf");
        var filterPath = Path.Combine(_tempDirectory, "filter.txt");
        File.WriteAllText(filterPath, "allowed\\.example\\.com\n");
        File.WriteAllText(configPath, $"Port 8888\nFilter {filterPath}\n");

        var snapshots = new List<Configuration>();
        using var reloader = new ConfigReloader(
            new NullLogger(),
            configPath,
            config => snapshots.Add(config),
            enableFileWatcher: false);

        reloader.ReloadConfig();

        File.WriteAllText(filterPath, "[invalid-regex\n");
        reloader.ReloadConfig();

        var config = Assert.Single(snapshots);
        Assert.Contains("allowed\\.example\\.com", config.FilterPatterns);
    }

    [Fact]
    public void ReloadConfig_WhenOnlyFilterFileChanges_StillReloads()
    {
        var configPath = Path.Combine(_tempDirectory, "tinyproxy.conf");
        var filterPath = Path.Combine(_tempDirectory, "filter.txt");
        File.WriteAllText(filterPath, "first\\.example\\.com\n");
        File.WriteAllText(configPath, $"Port 8888\nFilter {filterPath}\n");

        var snapshots = new List<Configuration>();
        using var reloader = new ConfigReloader(
            new NullLogger(),
            configPath,
            config => snapshots.Add(config),
            enableFileWatcher: false);

        reloader.ReloadConfig();

        File.WriteAllText(filterPath, "second\\.example\\.com\n");
        reloader.ReloadConfig();

        Assert.Equal(2, snapshots.Count);
        Assert.Contains("first\\.example\\.com", snapshots[0].FilterPatterns);
        Assert.Contains("second\\.example\\.com", snapshots[1].FilterPatterns);
    }

    [Fact]
    public void Constructor_WithRelativeConfigPath_EnablesWatcher()
    {
        var previousDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDirectory);

        try
        {
            File.WriteAllText("tinyproxy.conf", "Port 8888\n");

            using var reloader = new ConfigReloader(
                new NullLogger(),
                "tinyproxy.conf",
                _ => { },
                enableFileWatcher: true);

            Assert.True(reloader.IsEnabled);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    private sealed class NullLogger : ILogger
    {
        public void LogInfo(string message) { }
        public void LogError(string message) { }
        public void LogWarning(string message) { }
        public void LogConnect(string message) { }
        public void LogCritical(string message) { }
    }
}
