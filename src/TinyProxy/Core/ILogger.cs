namespace TinyProxy.Core;

/// <summary>
/// Simple logger interface for proxy operations.
/// </summary>
public interface ILogger
{
    /// <summary>
    /// Executes log info.
    /// </summary>
    void LogInfo(string message);
    /// <summary>
    /// Executes log error.
    /// </summary>
    void LogError(string message);
    /// <summary>
    /// Executes log warning.
    /// </summary>
    void LogWarning(string message);
    /// <summary>
    /// Executes log connect.
    /// </summary>
    void LogConnect(string message);
    /// <summary>
    /// Executes log critical.
    /// </summary>
    void LogCritical(string message);
}