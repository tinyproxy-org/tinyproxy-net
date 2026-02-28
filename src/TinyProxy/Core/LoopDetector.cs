namespace TinyProxy.Core;

/// <summary>
/// Detects and prevents proxy chaining loops.
/// Aligns with tinyproxy C's loop.c functionality.
/// </summary>
public sealed class LoopDetector
{
    private readonly TimeSpan _loopTimeout;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly List<LoopRecord> _records = new(32);
    private readonly object _syncRoot = new();

    private readonly struct LoopRecord
    {
        public required AddressFamily AddressFamily { get; init; }
        public required IPAddress Address { get; init; }
        public required int Port { get; init; }
        public required DateTimeOffset Timestamp { get; init; }
    }

    public LoopDetector() : this(TimeSpan.FromSeconds(15), static () => DateTimeOffset.UtcNow)
    {
    }

    public LoopDetector(TimeSpan loopTimeout, Func<DateTimeOffset> utcNow)
    {
        if (loopTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(loopTimeout));
        _loopTimeout = loopTimeout;
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    /// <summary>
    /// Records a local outbound endpoint when connecting to the proxy's own listen port.
    /// Later incoming client connections to the same endpoint are considered loops.
    /// </summary>
    public void RecordOutgoingLocalEndpoint(EndPoint? localEndpoint)
    {
        if (localEndpoint is not IPEndPoint ipEndPoint) return;
        var now = _utcNow();

        lock (_syncRoot)
        {
            PruneExpired(now);
            _records.Add(new LoopRecord
            {
                AddressFamily = ipEndPoint.AddressFamily,
                Address = ipEndPoint.Address,
                Port = ipEndPoint.Port,
                Timestamp = now
            });
        }
    }

    /// <summary>
    /// Checks whether an incoming client endpoint matches a recently recorded local outbound endpoint.
    /// This mirrors tinyproxy C's connection_loops() behavior.
    /// </summary>
    public bool IsLoopDetected(EndPoint? remoteEndpoint)
    {
        if (remoteEndpoint is not IPEndPoint remoteIp) return false;
        var now = _utcNow();

        lock (_syncRoot)
        {
            PruneExpired(now);

            for (var i = 0; i < _records.Count; i++)
            {
                var record = _records[i];
                if (record.AddressFamily != remoteIp.AddressFamily) continue;
                if (record.Port != remoteIp.Port) continue;
                if (record.Address.Equals(remoteIp.Address)) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Clears all loop detection records.
    /// Useful for testing or state reset.
    /// </summary>
    public void Clear()
    {
        lock (_syncRoot)
        {
            _records.Clear();
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        var cutoff = now - _loopTimeout;

        for (var i = _records.Count - 1; i >= 0; i--)
            if (_records[i].Timestamp < cutoff)
                _records.RemoveAt(i);
    }
}
