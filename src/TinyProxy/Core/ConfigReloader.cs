namespace TinyProxy.Core;

/// <summary>
/// Handles configuration hot-reload via file system watcher.
/// Aligns with tinyproxy C's SIGHUP handling in main.c.
///
/// Uses FileSystemWatcher to detect configuration file changes,
/// providing cross-platform hot-reload capability.
/// </summary>
public sealed class ConfigReloader : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _configPath;
    private readonly Action<Configuration> _reloadAction;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer? _debounceTimer;
    private DateTime _lastReloadTime = DateTime.MinValue;
    private const int ReloadDebounceMs = 500; // Debounce rapid file changes
    private const int MinReloadIntervalMs = 1000; // Minimum time between reloads
    private bool _disposed;
    private string? _lastConfigContent;
    private bool _lastConfigWasMissing;

    public ConfigReloader(
        ILogger logger,
        string configPath,
        Action<Configuration> reloadAction,
        bool enableFileWatcher = true)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configPath = configPath ?? throw new ArgumentNullException(nameof(configPath));
        _reloadAction = reloadAction ?? throw new ArgumentNullException(nameof(reloadAction));

        if (!enableFileWatcher) return;

        try
        {
            var directory = Path.GetDirectoryName(_configPath);
            var fileName = Path.GetFileName(_configPath);

            if (!string.IsNullOrEmpty(directory) && !string.IsNullOrEmpty(fileName) && Directory.Exists(directory))
            {
                _watcher = new FileSystemWatcher(directory, fileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };

                _watcher.Changed += OnConfigChanged;
                _watcher.Created += OnConfigChanged;

                // Debounce timer to handle rapid file changes (e.g., editor saves)
                _debounceTimer = new Timer(OnDebounceTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);

                _logger.LogInfo($"Configuration hot-reload enabled (watching {_configPath})");
            }
            else
            {
                _logger.LogWarning($"Configuration hot-reload not available: cannot watch {configPath}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Configuration hot-reload initialization failed: {ex.Message}");
        }
    }

    private void OnConfigChanged(object sender, FileSystemEventArgs e)
    {
        if (_disposed) return;

        // Check minimum interval between reloads
        var timeSinceLastReload = (DateTime.UtcNow - _lastReloadTime).TotalMilliseconds;
        if (timeSinceLastReload < MinReloadIntervalMs) return;

        // Start/reset debounce timer
        _debounceTimer?.Change(ReloadDebounceMs, Timeout.Infinite);
    }

    private void OnDebounceTimerElapsed(object? state)
    {
        if (_disposed) return;

        ReloadConfig();
    }

    /// <summary>
    /// Manually reloads the configuration.
    /// Can be called from any thread.
    /// </summary>
    public void ReloadConfig()
    {
        if (_disposed) return;

        try
        {
            // Update last reload time
            _lastReloadTime = DateTime.UtcNow;

            _logger.LogInfo($"Reloading configuration from {_configPath}");

            Configuration newConfig;
            string? configContent = null;
            var configMissing = false;

            if (File.Exists(_configPath))
            {
                configContent = File.ReadAllText(_configPath);
                newConfig = ConfigParser.Parse(configContent);
            }
            else
            {
                _logger.LogWarning($"Config file not found, using default configuration");
                newConfig = Configuration.Default;
                configMissing = true;
            }

            var configUnchanged =
                _lastConfigWasMissing == configMissing &&
                string.Equals(_lastConfigContent, configContent, StringComparison.Ordinal);

            if (configUnchanged)
            {
                _logger.LogInfo("Configuration unchanged, skipping reload");
                return;
            }

            _reloadAction(newConfig);
            _lastConfigContent = configContent;
            _lastConfigWasMissing = configMissing;
            _logger.LogInfo("Configuration reloaded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to reload configuration: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets whether configuration reload is available.
    /// </summary>
    public bool IsEnabled => _watcher != null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _debounceTimer?.Dispose();

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnConfigChanged;
            _watcher.Created -= OnConfigChanged;
            _watcher.Dispose();
        }
    }
}
