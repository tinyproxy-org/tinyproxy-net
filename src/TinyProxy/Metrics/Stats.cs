using System.Diagnostics;

namespace TinyProxy.Metrics;

/// <summary>
/// Proxy statistics and metrics.
/// </summary>
public sealed class Stats
{
    private long _totalConnections;
    private long _activeConnections;
    private long _totalRequests;
    private long _totalBytesSent;
    private long _totalBytesReceived;
    private long _failedRequests;

    /// <summary>
    /// Gets the total number of connections handled.
    /// </summary>
    public long TotalConnections => Interlocked.Read(ref _totalConnections);

    /// <summary>
    /// Gets the current number of active connections.
    /// </summary>
    public long ActiveConnections => Interlocked.Read(ref _activeConnections);

    /// <summary>
    /// Gets the total number of requests processed.
    /// </summary>
    public long TotalRequests => Interlocked.Read(ref _totalRequests);

    /// <summary>
    /// Gets the total bytes sent to clients.
    /// </summary>
    public long TotalBytesSent => Interlocked.Read(ref _totalBytesSent);

    /// <summary>
    /// Gets the total bytes received from clients.
    /// </summary>
    public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);

    /// <summary>
    /// Gets the number of failed requests.
    /// </summary>
    public long FailedRequests => Interlocked.Read(ref _failedRequests);

    /// <summary>
    /// Increments the total connection count.
    /// </summary>
    public void IncrementConnections()
    {
        Interlocked.Increment(ref _totalConnections);
        Interlocked.Increment(ref _activeConnections);
    }

    /// <summary>
    /// Decrements the active connection count.
    /// </summary>
    public void DecrementActiveConnections()
    {
        Interlocked.Decrement(ref _activeConnections);
    }

    /// <summary>
    /// Increments the total request count.
    /// </summary>
    public void IncrementRequests()
    {
        Interlocked.Increment(ref _totalRequests);
    }

    /// <summary>
    /// Adds bytes sent.
    /// </summary>
    public void AddBytesSent(long bytes)
    {
        Interlocked.Add(ref _totalBytesSent, bytes);
    }

    /// <summary>
    /// Adds bytes received.
    /// </summary>
    public void AddBytesReceived(long bytes)
    {
        Interlocked.Add(ref _totalBytesReceived, bytes);
    }

    /// <summary>
    /// Increments the failed request count.
    /// </summary>
    public void IncrementFailedRequests()
    {
        Interlocked.Increment(ref _failedRequests);
    }

    /// <summary>
    /// Gets a snapshot of current statistics.
    /// </summary>
    public StatsSnapshot GetSnapshot()
    {
        return new StatsSnapshot(
            TotalConnections,
            ActiveConnections,
            TotalRequests,
            TotalBytesSent,
            TotalBytesReceived,
            FailedRequests);
    }

    /// <summary>
    /// Resets all statistics.
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _totalConnections, 0);
        Interlocked.Exchange(ref _activeConnections, 0);
        Interlocked.Exchange(ref _totalRequests, 0);
        Interlocked.Exchange(ref _totalBytesSent, 0);
        Interlocked.Exchange(ref _totalBytesReceived, 0);
        Interlocked.Exchange(ref _failedRequests, 0);
    }
}

/// <summary>
/// Snapshot of statistics at a point in time.
/// </summary>
public sealed record StatsSnapshot(
    long TotalConnections,
    long ActiveConnections,
    long TotalRequests,
    long TotalBytesSent,
    long TotalBytesReceived,
    long FailedRequests)
{
    public override string ToString()
    {
        return $"Stats: Connections={TotalConnections} (Active={ActiveConnections}), " +
               $"Requests={TotalRequests}, " +
               $"Bytes: Sent={TotalBytesSent}, Received={TotalBytesReceived}, " +
               $"Failed={FailedRequests}";
    }
}
