namespace TinyProxy.Logging;

/// <summary>
/// Structured logging with component context and log levels.
/// Provides domain-specific logging methods.
/// </summary>
public interface IStructuredLogger
{
    /// <summary>
    /// Executes log debug.
    /// </summary>
    void LogDebug(string message, params object?[] args);

    /// <summary>
    /// Executes log info.
    /// </summary>
    void LogInfo(string message, params object?[] args);

    /// <summary>
    /// Executes log warning.
    /// </summary>
    void LogWarning(string message, params object?[] args);

    /// <summary>
    /// Executes log error.
    /// </summary>
    void LogError(string message, Exception? ex, params object?[] args);

    /// <summary>
    /// Executes log critical.
    /// </summary>
    void LogCritical(string message);

    /// <summary>
    /// Executes log connection.
    /// </summary>
    void LogConnection(string direction, string endpoint);

    /// <summary>
    /// Executes log request.
    /// </summary>
    void LogRequest(string method, string uri, string version);

    /// <summary>
    /// Executes log access.
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