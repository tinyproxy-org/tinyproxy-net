using System.Diagnostics;
using System.Text;
using TinyProxy.Config;
using TinyProxy.Core;

namespace TinyProxy.Logging;

/// <summary>
/// Shared lock for console output to prevent interleaved writes.
/// </summary>
internal static class ConsoleLock
{
    internal static readonly object Lock = new();
}

/// <summary>
/// Access logger in Apache/Common Log Format.
/// Format: host - - [date] "request" status bytes
/// </summary>
public sealed class AccessLogger : IDisposable
{
    private readonly Configuration _config;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private StreamWriter? _writer;

    public AccessLogger(Configuration config, ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        InitializeWriter();
    }

    private void InitializeWriter()
    {
        if (!string.IsNullOrEmpty(_config.LogFile))
        {
            try
            {
                var directory = Path.GetDirectoryName(_config.LogFile);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                _writer = new StreamWriter(_config.LogFile, append: true)
                {
                    AutoFlush = true
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to initialize log file '{_config.LogFile}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Logs an access event.
    /// </summary>
    public void LogAccess(string clientIp, string method, string uri, string version, int statusCode, long bytesSent)
    {
        var timestamp = DateTime.Now.ToString("dd/MMM/yyyy:HH:mm:ss zz00");

        // Sanitize inputs to prevent log injection
        var safeIp = SanitizeLogValue(clientIp);
        var safeUri = SanitizeLogValue(uri);
        var safeMethod = SanitizeLogValue(method);
        var safeVersion = SanitizeLogValue(version);

        var logEntry = $"{safeIp} - - [{timestamp}] \"{safeMethod} {safeUri} {safeVersion}\" {statusCode} {bytesSent}";

        lock (_lock)
        {
            if (_writer != null)
            {
                try
                {
                    _writer.WriteLine(logEntry);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed to write to log file: {ex.Message}");
                }
            }
            else
            {
                // Write directly to stdout without prefix (like tinyproxy C)
                // Use shared lock to prevent interleaving with ConsoleLogger output
                lock (ConsoleLock.Lock)
                {
                    Console.WriteLine(logEntry);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    /// <summary>
    /// Logs a CONNECT request.
    /// </summary>
    public void LogConnect(string clientIp, string host, int port, bool success)
    {
        var method = "CONNECT";
        var uri = $"{host}:{port}";
        var statusCode = success ? 200 : 502;
        LogAccess(clientIp, method, uri, "HTTP/1.1", statusCode, 0);
    }

    /// <summary>
    /// Sanitizes a value for logging to prevent log injection attacks.
    /// Removes newlines, carriage returns, and other control characters.
    /// </summary>
    private static string SanitizeLogValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "-";
        }

        var sb = new StringBuilder(value.Length);

        foreach (var c in value)
        {
            // Allow printable ASCII and common Unicode printable characters
            // Skip control characters and newlines
            if (c == '\r' || c == '\n' || c == '\t' || char.IsControl(c))
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
