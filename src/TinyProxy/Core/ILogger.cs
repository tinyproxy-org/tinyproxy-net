namespace TinyProxy.Core;

/// <summary>
/// Simple logger interface for proxy operations.
/// </summary>
public interface ILogger
{
    void LogInfo(string message);
    void LogError(string message);
    void LogWarning(string message);
    void LogConnect(string message);
    void LogCritical(string message);
}
