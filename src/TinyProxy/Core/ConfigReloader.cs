namespace TinyProxy.Core;

/// <summary>
/// Handles configuration hot-reload via file system watcher.
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
    private string? _lastConfigSignature;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigReloader"/> class.
    /// </summary>
    public ConfigReloader(
        ILogger logger,
        string configPath,
        Action<Configuration> reloadAction,
        bool enableFileWatcher = true)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configPath = Path.GetFullPath(configPath ?? throw new ArgumentNullException(nameof(configPath)));
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
                _logger.LogWarning($"Configuration hot-reload not available: cannot watch {_configPath}");
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

        var timeSinceLastReload = (DateTime.UtcNow - _lastReloadTime).TotalMilliseconds;
        if (timeSinceLastReload < MinReloadIntervalMs) return;

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
            _lastReloadTime = DateTime.UtcNow;

            _logger.LogInfo($"Reloading configuration from {_configPath}");

            if (!File.Exists(_configPath))
            {
                _logger.LogWarning($"Config file not found, keeping last known good configuration");
                return;
            }

            var configContent = File.ReadAllText(_configPath);
            var newConfig = ConfigParser.Parse(configContent);
            var configSignature = BuildConfigSignature(configContent, newConfig);

            var configUnchanged = string.Equals(_lastConfigSignature, configSignature, StringComparison.Ordinal);

            if (configUnchanged)
            {
                _logger.LogInfo("Configuration unchanged, skipping reload");
                return;
            }

            _reloadAction(newConfig);
            _lastConfigSignature = configSignature;
            _logger.LogInfo("Configuration reloaded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to reload configuration: {ex.Message}");
        }
    }

    private string BuildConfigSignature(string configContent, Configuration config)
    {
        if (string.IsNullOrWhiteSpace(config.FilterFile)) return configContent;

        var filterPath = ResolveFilterFilePath(config.FilterFile);
        if (!File.Exists(filterPath)) return configContent;

        try
        {
            var filterContent = File.ReadAllText(filterPath);
            return string.Concat(configContent, "\n#FILTER#", filterPath, "\n", filterContent);
        }
        catch
        {
            return configContent;
        }
    }

    private string ResolveFilterFilePath(string filterFile)
    {
        if (Path.IsPathRooted(filterFile)) return filterFile;

        var baseDir = Path.GetDirectoryName(_configPath);
        if (string.IsNullOrEmpty(baseDir)) return filterFile;

        return Path.Combine(baseDir, filterFile);
    }

    /// <summary>
    /// Gets a value indicating whether enabled.
    /// </summary>
    public bool IsEnabled => _watcher != null;

    /// <summary>
    /// Releases the resources used by this instance.
    /// </summary>
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
