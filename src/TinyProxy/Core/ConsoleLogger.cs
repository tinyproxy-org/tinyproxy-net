namespace TinyProxy.Core;

/// <summary>
/// Console-based logger implementation.
/// </summary>
public sealed class ConsoleLogger : ILogger
{
    private readonly string _prefix;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConsoleLogger"/> class.
    /// </summary>
    public ConsoleLogger(string prefix = "TinyProxy")
    {
        _prefix = prefix;
    }

    private void WriteLog(string level, string message)
    {
        // Use shared lock to prevent interleaving with AccessLogger output
        lock (ConsoleLock.Lock)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Console.WriteLine($"[{timestamp}] [{_prefix}] [{level}] {message}");
        }
    }

    /// <summary>
    /// Executes log info.
    /// </summary>
    public void LogInfo(string message)
    {
        WriteLog("INFO", message);
    }

    /// <summary>
    /// Executes log error.
    /// </summary>
    public void LogError(string message)
    {
        WriteLog("ERROR", message);
    }

    /// <summary>
    /// Executes log warning.
    /// </summary>
    public void LogWarning(string message)
    {
        WriteLog("WARN", message);
    }

    /// <summary>
    /// Executes log connect.
    /// </summary>
    public void LogConnect(string message)
    {
        WriteLog("CONNECT", message);
    }

    /// <summary>
    /// Executes log critical.
    /// </summary>
    public void LogCritical(string message)
    {
        WriteLog("CRITICAL", message);
    }
}