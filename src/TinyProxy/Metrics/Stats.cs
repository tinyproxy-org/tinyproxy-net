namespace TinyProxy.Metrics;

/// <summary>
/// Proxy statistics and metrics.
/// Aligns with tinyproxy C's stats.c
/// </summary>
public sealed class Stats
{
    private long _totalConnections;
    private long _activeConnections;
    private long _totalRequests;
    private long _totalBytesSent;
    private long _totalBytesReceived;
    private long _failedRequests;
    private long _deniedRequests;
    private long _refusedConnections;

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
    public long BytesSent => Interlocked.Read(ref _totalBytesSent);

    /// <summary>
    /// Gets the total bytes received from clients.
    /// </summary>
    public long BytesReceived => Interlocked.Read(ref _totalBytesReceived);

    /// <summary>
    /// Alias for BytesSent for compatibility.
    /// </summary>
    public long TotalBytesSent => BytesSent;

    /// <summary>
    /// Alias for BytesReceived for compatibility.
    /// </summary>
    public long TotalBytesReceived => BytesReceived;

    /// <summary>
    /// Gets the number of failed requests.
    /// Aligns with tinyproxy C's STAT_BADCONN.
    /// </summary>
    public long FailedRequests => Interlocked.Read(ref _failedRequests);

    /// <summary>
    /// Gets the number of denied requests (access control).
    /// Aligns with tinyproxy C's STAT_DENIED.
    /// </summary>
    public long DeniedRequests => Interlocked.Read(ref _deniedRequests);

    /// <summary>
    /// Gets the number of refused connections (overload).
    /// Aligns with tinyproxy C's STAT_REFUSE.
    /// </summary>
    public long RefusedConnections => Interlocked.Read(ref _refusedConnections);

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
    /// Increments the denied request count.
    /// Aligns with tinyproxy C's STAT_DENIED.
    /// </summary>
    public void IncrementDeniedRequests()
    {
        Interlocked.Increment(ref _deniedRequests);
    }

    /// <summary>
    /// Increments the refused connections count.
    /// Aligns with tinyproxy C's STAT_REFUSE.
    /// </summary>
    public void IncrementRefusedConnections()
    {
        Interlocked.Increment(ref _refusedConnections);
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
            BytesSent,
            BytesReceived,
            FailedRequests,
            DeniedRequests,
            RefusedConnections);
    }
}

/// <summary>
/// Snapshot of statistics at a point in time.
/// </summary>
public sealed record StatsSnapshot(
    long TotalConnections,
    long ActiveConnections,
    long TotalRequests,
    long BytesSent,
    long BytesReceived,
    long FailedRequests,
    long DeniedRequests = 0,
    long RefusedConnections = 0)
{
    public override string ToString()
    {
        return $"Stats: Connections={TotalConnections} (Active={ActiveConnections}), " +
               $"Requests={TotalRequests}, " +
               $"Bytes: Sent={BytesSent}, Received={BytesReceived}, " +
               $"Failed={FailedRequests}, Denied={DeniedRequests}, Refused={RefusedConnections}";
    }
}