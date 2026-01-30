using System.Net;
using System.Net.Sockets;
using TinyProxy.Config;
using TinyProxy.Core;
using TinyProxy.Logging;
using TinyProxy.Metrics;

namespace TinyProxy;

class Program
{
    static async Task Main(string[] args)
    {
        var config = LoadConfiguration(args);
        var logger = new ConsoleLogger();
        var stats = new Stats();
        var accessLogger = new AccessLogger(config, logger);
        var connectionManager = new ConnectionManager(config, logger);

        logger.LogInfo($"TinyProxy.NET starting on {config.ListenAddress}:{config.ListenPort}");

        var eventLoop = new EventLoop(
            IPAddress.Parse(config.ListenAddress),
            config.ListenPort,
            (socket) => HandleConnectionAsync(socket, config, logger, stats, accessLogger),
            logger,
            connectionManager,
            config
        );

        eventLoop.Start();

        logger.LogInfo("Press Ctrl+C to exit...");

        // Wait for Ctrl+C
        var tcs = new TaskCompletionSource<bool>();
        Console.CancelKeyPress += async (s, e) =>
        {
            e.Cancel = true;
            logger.LogInfo($"Shutting down... Final stats: {stats.GetSnapshot()}");
            await CleanupAsync(eventLoop, accessLogger);
            tcs.TrySetResult(true);
        };

        // Add AppDomain unload handler for non-Ctrl+C shutdown paths
        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            // ProcessExit is synchronous, do sync cleanup
            Cleanup(eventLoop, accessLogger);
        };

        await tcs.Task;
    }

    private static async Task CleanupAsync(EventLoop eventLoop, AccessLogger accessLogger)
    {
        try
        {
            eventLoop?.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during event loop disposal: {ex.Message}");
        }

        try
        {
            accessLogger?.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during access logger disposal: {ex.Message}");
        }

        // Ensure we yield back to the call context
        await Task.Yield();
    }

    private static void Cleanup(EventLoop eventLoop, AccessLogger accessLogger)
    {
        try
        {
            eventLoop?.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during event loop disposal: {ex.Message}");
        }

        try
        {
            accessLogger?.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during access logger disposal: {ex.Message}");
        }
    }

    private static Configuration LoadConfiguration(string[] args)
    {
        // Check for config file argument
        var configPath = args.FirstOrDefault(a => !a.StartsWith("-")) ?? "tinyproxy.conf";

        if (File.Exists(configPath))
        {
            return ConfigParser.LoadFromFile(configPath);
        }

        // Return default configuration
        return Configuration.Default;
    }

    private static async ValueTask HandleConnectionAsync(
        Socket socket,
        Configuration config,
        ILogger logger,
        Stats stats,
        AccessLogger accessLogger)
    {
        stats.IncrementConnections();

        try
        {
            using var connection = new Connection(socket, logger, config, stats, accessLogger);
            await connection.ProcessAsync();
        }
        finally
        {
            stats.DecrementActiveConnections();
        }
    }
}
