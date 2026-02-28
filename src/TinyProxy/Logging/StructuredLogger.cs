namespace TinyProxy.Logging;

/// <summary>
/// Structured logging with component context and log levels.
/// Provides domain-specific logging methods.
/// </summary>
public interface IStructuredLogger
{
    /// <summary>
    /// Logs a debug message.
    /// </summary>
    void LogDebug(string message, params object?[] args);

    /// <summary>
    /// Logs an info message.
    /// </summary>
    void LogInfo(string message, params object?[] args);

    /// <summary>
    /// Logs a warning message.
    /// </summary>
    void LogWarning(string message, params object?[] args);

    /// <summary>
    /// Logs an error message with optional exception.
    /// </summary>
    void LogError(string message, Exception? ex, params object?[] args);

    /// <summary>
    /// Logs a critical message.
    /// </summary>
    void LogCritical(string message);

    /// <summary>
    /// Logs a connection event.
    /// </summary>
    void LogConnection(string direction, string endpoint);

    /// <summary>
    /// Logs an HTTP request.
    /// </summary>
    void LogRequest(string method, string uri, string version);

    /// <summary>
    /// Logs an access event.
    /// </summary>
    void LogAccess(string clientIp, string method, string uri, int statusCode, long bytes);
}

/// <summary>
/// Log levels for filtering log output.
/// </summary>
public enum LogLevel
{
    /// <summary>
    /// Detailed debugging information.
    /// </summary>
    Debug = 0,

    /// <summary>
    /// General informational messages.
    /// </summary>
    Info = 1,

    /// <summary>
    /// Warning messages for potentially harmful situations.
    /// </summary>
    Warning = 2,

    /// <summary>
    /// Error conditions.
    /// </summary>
    Error = 3,

    /// <summary>
    /// Critical conditions requiring immediate attention.
    /// </summary>
    Critical = 4
}