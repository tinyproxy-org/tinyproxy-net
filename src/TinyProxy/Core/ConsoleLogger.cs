namespace TinyProxy.Core;

/// <summary>
/// Console-based logger implementation.
/// </summary>
public sealed class ConsoleLogger : ILogger
{
    private readonly string _prefix;

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

    public void LogInfo(string message)
    {
        WriteLog("INFO", message);
    }

    public void LogError(string message)
    {
        WriteLog("ERROR", message);
    }

    public void LogWarning(string message)
    {
        WriteLog("WARN", message);
    }

    public void LogConnect(string message)
    {
        WriteLog("CONNECT", message);
    }

    public void LogCritical(string message)
    {
        WriteLog("CRITICAL", message);
    }
}