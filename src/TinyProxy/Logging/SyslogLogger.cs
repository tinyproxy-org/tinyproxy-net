using System;
using System.Net.Sockets;
using System.Text;
using TinyProxy.Core;

namespace TinyProxy.Logging;

/// <summary>
/// Syslog logger implementation.
/// Aligns with tinyproxy C's syslog support in log.c.
///
/// Supports RFC 5424 syslog format over UDP.
/// </summary>
public sealed class SyslogLogger : ILogger, IDisposable
{
    private readonly string _hostname;
    private readonly string _appName;
    private readonly UdpClient? _udpClient;
    private readonly string? _server;
    private readonly int _port;
    private readonly bool _isEnabled;
    private bool _disposed;

    // Syslog severity levels
    private const int SeverityEmergency = 0;
    private const int SeverityAlert = 1;
    private const int SeverityCritical = 2;
    private const int SeverityError = 3;
    private const int SeverityWarning = 4;
    private const int SeverityNotice = 5;
    private const int SeverityInfo = 6;
    private const int SeverityDebug = 7;

    // Default facility (user-level messages)
    private const int FacilityUser = 1;

    public SyslogLogger(string? server = null, int port = 514, string? appName = null)
    {
        _server = server;
        _port = port;
        _appName = appName ?? "TinyProxy.NET";
        _hostname = Environment.MachineName;

        if (!string.IsNullOrEmpty(server))
            try
            {
                _udpClient = new UdpClient();
                _udpClient.Connect(server, port);
                _isEnabled = true;
            }
            catch
            {
                _isEnabled = false;
            }
    }

    public void LogCritical(string message)
    {
        Log(SeverityCritical, "CRITICAL", message);
    }

    public void LogError(string message)
    {
        Log(SeverityError, "ERROR", message);
    }

    public void LogWarning(string message)
    {
        Log(SeverityWarning, "WARNING", message);
    }

    public void LogNotice(string message)
    {
        Log(SeverityNotice, "NOTICE", message);
    }

    public void LogInfo(string message)
    {
        Log(SeverityInfo, "INFO", message);
    }

    public void LogConnect(string message)
    {
        Log(SeverityInfo, "CONNECT", message);
    }

    private void Log(int severity, string level, string message)
    {
        if (!_isEnabled || _udpClient == null || _disposed) return;

        try
        {
            var syslogMessage = FormatSyslogMessage(severity, level, message);
            var buffer = Encoding.UTF8.GetBytes(syslogMessage);
            _udpClient.Send(buffer, buffer.Length);
        }
        catch
        {
            // Silently fail to avoid logging loops
        }
    }

    /// <summary>
    /// Formats a message according to RFC 5424 syslog format.
    /// Format: <PRIVAL>VERSION TIMESTAMP HOSTNAME APP-NAME PROCID MSGID STRUCTURED-DATA MSG
    /// </summary>
    private string FormatSyslogMessage(int severity, string level, string message)
    {
        // Calculate PRI value: (facility * 8) + severity
        var pri = FacilityUser * 8 + severity;

        // Timestamp in ISO 8601 format
        var timestamp = DateTime.UtcNow.ToString("o");

        // Escape message for syslog
        var escapedMessage = message.Replace("\\", "\\\\").Replace("]", "\\]");

        return $"<{pri}>1 {timestamp} {_hostname} {_appName} - - - [{level}] {escapedMessage}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _udpClient?.Dispose();
    }
}