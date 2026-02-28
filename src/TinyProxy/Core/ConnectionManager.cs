using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly ConcurrentDictionary<string, int> _activeConnectionsByIp = new(StringComparer.Ordinal);
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
        if (!await _semaphore.WaitAsync(TimeSpan.Zero, token).ConfigureAwait(false))
        {
            _logger.LogWarning($"Connection limit reached ({_config.MaxClients})");
            return null;
        }

        var normalizedClientIp = NormalizeClientIp(clientIp);
        if (!TryAcquirePerIpSlot(normalizedClientIp))
        {
            _semaphore.Release();
            _logger.LogWarning($"Per-IP connection limit reached ({_config.MaxClientsPerIp}) for {normalizedClientIp}");
            return null;
        }

        Interlocked.Increment(ref _totalActiveConnections);

        return new ConnectionSlot(this, normalizedClientIp);
    }

    /// <summary>
    /// Releases a connection slot.
    /// </summary>
    internal void ReleaseSlot(string? clientIp)
    {
        if (_config.MaxClientsPerIp > 0 && clientIp != null) DecrementPerIpCount(clientIp);

        Interlocked.Decrement(ref _totalActiveConnections);
        _semaphore.Release();
    }

    private static string? NormalizeClientIp(string? clientIp)
    {
        if (string.IsNullOrWhiteSpace(clientIp)) return null;
        return clientIp.Trim();
    }

    private bool TryAcquirePerIpSlot(string? clientIp)
    {
        if (_config.MaxClientsPerIp <= 0 || clientIp == null) return true;

        var newCount = _activeConnectionsByIp.AddOrUpdate(
            clientIp,
            static _ => 1,
            static (_, current) => current + 1);

        if (newCount <= _config.MaxClientsPerIp) return true;

        // Roll back this acquisition attempt.
        DecrementPerIpCount(clientIp);
        return false;
    }

    private void DecrementPerIpCount(string clientIp)
    {
        while (true)
        {
            if (!_activeConnectionsByIp.TryGetValue(clientIp, out var current)) return;

            if (current <= 1)
            {
                if (_activeConnectionsByIp.TryRemove(clientIp, out _)) return;
                continue;
            }

            if (_activeConnectionsByIp.TryUpdate(clientIp, current - 1, current)) return;
        }
    }
}

/// <summary>
/// Represents an acquired connection slot. Release when done.
/// </summary>
public sealed class ConnectionSlot : IDisposable
{
    private readonly ConnectionManager _manager;
    private readonly string? _clientIp;
    private bool _disposed;

    public ConnectionSlot(ConnectionManager manager, string? clientIp)
    {
        _manager = manager;
        _clientIp = clientIp;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _manager.ReleaseSlot(_clientIp);
    }
}
