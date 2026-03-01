namespace TinyProxy.Logging;

/// <summary>
/// Implementation of structured logger with component context.
/// Wraps base ILogger with enhanced formatting and domain-specific methods.
/// </summary>
public sealed class StructuredLogger : IStructuredLogger
{
    private readonly ILogger _target;
    private readonly string _component;

    /// <summary>
    /// Initializes a new instance of the <see cref="StructuredLogger"/> class.
    /// </summary>
    public StructuredLogger(ILogger target, string component)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _component = component;
    }

    /// <summary>
    /// Executes log debug.
    /// </summary>
    public void LogDebug(string message, params object?[] args)
    {
        Write("DEBUG", message, args);
    }

    /// <summary>
    /// Executes log info.
    /// </summary>
    public void LogInfo(string message, params object?[] args)
    {
        Write("INFO", message, args);
    }

    /// <summary>
    /// Executes log warning.
    /// </summary>
    public void LogWarning(string message, params object?[] args)
    {
        Write("WARNING", message, args);
    }

    /// <summary>
    /// Executes log error.
    /// </summary>
    public void LogError(string message, Exception? ex, params object?[] args)
    {
        var fullMessage = ex != null
            ? $"{message}: {ex.Message}"
            : message;

        Write("ERROR", fullMessage);
    }

    /// <summary>
    /// Executes log critical.
    /// </summary>
    public void LogCritical(string message)
    {
        Write("CRITICAL", message);
    }

    /// <summary>
    /// Executes log connection.
    /// </summary>
    public void LogConnection(string direction, string endpoint)
    {
        LogInfo($"[{_component}] CONN {direction}: {endpoint}");
    }

    /// <summary>
    /// Executes log request.
    /// </summary>
    public void LogRequest(string method, string uri, string version)
    {
        LogInfo($"[{_component}] REQ {method} {uri} {version}");
    }

    /// <summary>
    /// Executes log access.
    /// </summary>
    public void LogAccess(string clientIp, string method, string uri, int statusCode, long bytes)
    {
        LogInfo($"[{_component}] ACCESS {clientIp} {method} {uri} -> {statusCode} {bytes}");
    }

    /// <summary>
    /// Writes a formatted log message to the target logger.
    /// </summary>
    private void Write(string level, string message, params object?[] args)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var formatted = args?.Length > 0
            ? string.Format($"[{timestamp}] {_component} [{level}] {message}", args)
            : $"[{timestamp}] {_component} [{level}] {message}";

        _target.LogInfo(formatted);
    }
}