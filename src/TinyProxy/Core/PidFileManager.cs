using System.Diagnostics;

namespace TinyProxy.Core;

/// <summary>
/// Manages PID file writing and cleanup.
/// Aligns with tinyproxy C's pidfile handling.
/// </summary>
public sealed class PidFileManager : IDisposable
{
    private readonly ILogger _logger;
    private readonly string? _pidFilePath;
    private bool _disposed;

    public PidFileManager(ILogger logger, string? pidFilePath)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pidFilePath = pidFilePath;

        if (!string.IsNullOrEmpty(_pidFilePath)) WritePidFile();
    }

    private void WritePidFile()
    {
        try
        {
            var pid = Process.GetCurrentProcess().Id;

            // Ensure directory exists
            var directory = Path.GetDirectoryName(_pidFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);

            if (!string.IsNullOrEmpty(_pidFilePath))
            {
                File.WriteAllText(_pidFilePath, pid.ToString());
                _logger.LogInfo($"PID file written: {_pidFilePath} (PID {pid})");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to write PID file: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!string.IsNullOrEmpty(_pidFilePath))
            try
            {
                if (File.Exists(_pidFilePath))
                {
                    File.Delete(_pidFilePath);
                    _logger.LogInfo($"PID file removed: {_pidFilePath}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to remove PID file: {ex.Message}");
            }
    }
}