using TinyProxy.Config;

namespace TinyProxy.Core;

/// <summary>
/// Manages connection pool and enforces concurrent connection limits.
/// </summary>
public sealed class ConnectionManager
{
    private readonly Configuration _config;
    private readonly SemaphoreSlim _semaphore;
    private readonly ILogger _logger;
    private int _totalActiveConnections = 0;

    public ConnectionManager(Configuration config, ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _semaphore = new SemaphoreSlim(_config.MaxClients, _config.MaxClients);
    }

    /// <summary>
    /// Gets the current number of active connections.
    /// </summary>
    public int ActiveCount => _totalActiveConnections;

    /// <summary>
    /// Gets the maximum allowed concurrent connections.
    /// </summary>
    public int MaxClients => _config.MaxClients;

    /// <summary>
    /// Gets the maximum allowed concurrent connections per IP.
    /// </summary>
    public int MaxClientsPerIp => _config.MaxClientsPerIp;

    /// <summary>
    /// Tries to acquire a connection slot. Returns null if limit reached.
    /// </summary>
    public async Task<ConnectionSlot?> TryAcquireSlotAsync(string? clientIp, CancellationToken token)
    {
        // Note: Per-IP limiting removed to reduce memory allocations
        // The semaphore provides overall connection limiting

        if (!await _semaphore.WaitAsync(TimeSpan.Zero, token).ConfigureAwait(false))
        {
            _logger.LogWarning($"Connection limit reached ({_config.MaxClients})");
            return null;
        }

        Interlocked.Increment(ref _totalActiveConnections);

        return new ConnectionSlot(this);
    }

    /// <summary>
    /// Releases a connection slot.
    /// </summary>
    internal void ReleaseSlot()
    {
        Interlocked.Decrement(ref _totalActiveConnections);
        _semaphore.Release();
    }
}

/// <summary>
/// Represents an acquired connection slot. Release when done.
/// </summary>
public sealed class ConnectionSlot : IDisposable
{
    private readonly ConnectionManager _manager;
    private bool _disposed;

    public ConnectionSlot(ConnectionManager manager)
    {
        _manager = manager;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _manager.ReleaseSlot();
    }
}
