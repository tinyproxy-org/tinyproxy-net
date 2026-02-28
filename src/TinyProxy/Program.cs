namespace TinyProxy;

internal class Program
{
    private static async Task Main(string[] args)
    {
        var configPath = GetConfigPath(args);
        var config = LoadConfiguration(configPath);

        // Create logger (syslog if configured, otherwise console)
        var logger = CreateLogger(config);

        logger.LogInfo($"TinyProxy.NET starting on {config.ListenAddress}:{config.ListenPort}");

        var stats = new Stats();
        var accessLogger = new AccessLogger(config, logger);
        var connectionManager = new ConnectionManager(config, logger);

        // Shared loop detector for all connections
        // Aligns with tinyproxy C's loop.c functionality
        var loopDetector = new LoopDetector();

        // Initialize error pages with configuration
        // Aligns with tinyproxy C's html-error.c functionality
        Protocol.HtmlErrorPages.Initialize(config);

        // PID file management - aligns with tinyproxy C's pidfile
        var pidFileManager = new PidFileManager(logger, config.PidFile);

        // Configuration hot-reload - aligns with tinyproxy C's SIGHUP handling
        var currentConfig = config;
        var configReloader = new ConfigReloader(logger, configPath, newConfig =>
        {
            currentConfig = newConfig;
            // Reinitialize error pages with new configuration
            Protocol.HtmlErrorPages.Initialize(newConfig);
            logger.LogInfo("Configuration reloaded");
        });
        // ConfigReloader starts automatically in constructor

        // Use a wrapper for config access that can be updated
        var configAccessor = new ConfigurationAccessor(() => currentConfig);

        var eventLoop = new EventLoop(
            IPAddress.Parse(currentConfig.ListenAddress),
            currentConfig.ListenPort,
            (socket) => HandleConnectionAsync(socket, configAccessor, logger, stats, accessLogger, loopDetector),
            logger,
            connectionManager,
            currentConfig
        );

        eventLoop.Start();

        logger.LogInfo("Press Ctrl+C to exit...");

        // Wait for Ctrl+C
        var tcs = new TaskCompletionSource<bool>();
        Console.CancelKeyPress += async (s, e) =>
        {
            e.Cancel = true;
            logger.LogInfo($"Shutting down... Final stats: {stats.GetSnapshot()}");
            await CleanupAsync(eventLoop, accessLogger, configReloader, pidFileManager);
            tcs.TrySetResult(true);
        };

        // Add AppDomain unload handler for non-Ctrl+C shutdown paths
        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            // ProcessExit is synchronous, do sync cleanup
            Cleanup(eventLoop, accessLogger, configReloader, pidFileManager);
        };

        await tcs.Task;
    }

    private static string GetConfigPath(string[] args)
    {
        // Check for config file argument
        // Supports: -c filename or just filename
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "-c" && i + 1 < args.Length) return args[i + 1];

            if (!args[i].StartsWith("-")) return args[i];
        }

        return "tinyproxy.conf";
    }

    private static Configuration LoadConfiguration(string configPath)
    {
        if (File.Exists(configPath)) return ConfigParser.LoadFromFile(configPath);

        // Return default configuration
        return Configuration.Default;
    }

    private static ILogger CreateLogger(Configuration config)
    {
        if (config.UseSyslog) return new SyslogLogger(config.SyslogServer ?? "localhost", config.SyslogPort, "TinyProxy.NET");

        return new ConsoleLogger();
    }

    private static void Cleanup(
        EventLoop eventLoop,
        AccessLogger accessLogger,
        ConfigReloader configReloader,
        PidFileManager pidFileManager)
    {
        DisposeSafely(eventLoop, "event loop");
        DisposeSafely(accessLogger, "access logger");
        DisposeSafely(configReloader, "config reloader");
        DisposeSafely(pidFileManager, "PID file manager");
    }

    private static Task CleanupAsync(
        EventLoop eventLoop,
        AccessLogger accessLogger,
        ConfigReloader configReloader,
        PidFileManager pidFileManager)
    {
        Cleanup(eventLoop, accessLogger, configReloader, pidFileManager);
        return Task.CompletedTask;
    }

    private static void DisposeSafely(IDisposable? disposable, string name)
    {
        if (disposable == null) return;

        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during {name} disposal: {ex.Message}");
        }
    }

    private static async ValueTask HandleConnectionAsync(
        Socket socket,
        ConfigurationAccessor configAccessor,
        ILogger logger,
        Stats stats,
        AccessLogger accessLogger,
        LoopDetector loopDetector)
    {
        stats.IncrementConnections();

        try
        {
            // Get current config (supports hot-reload)
            var config = configAccessor.GetCurrent();

            using var connection = new Connection(socket, logger, config, stats, accessLogger, loopDetector);
            await connection.ProcessAsync();
        }
        finally
        {
            stats.DecrementActiveConnections();
        }
    }

    /// <summary>
    /// Provides thread-safe access to the current configuration.
    /// Allows configuration to be updated without restarting the service.
    /// </summary>
    private sealed class ConfigurationAccessor
    {
        private readonly Func<Configuration> _getConfig;

        public ConfigurationAccessor(Func<Configuration> getConfig)
        {
            _getConfig = getConfig ?? throw new ArgumentNullException(nameof(getConfig));
        }

        public Configuration GetCurrent()
        {
            return _getConfig();
        }
    }
}
