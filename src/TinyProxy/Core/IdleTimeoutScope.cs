namespace TinyProxy.Core;

/// <summary>
/// Tracks per-connection idle I/O timeout and exposes a linked cancellation token.
/// </summary>
internal sealed class IdleTimeoutScope : IDisposable
{
    private readonly CancellationToken _outerToken;
    private readonly CancellationTokenSource? _linkedTokenSource;
    private readonly TimeSpan _idleTimeout;
    private readonly object? _sync;

    public IdleTimeoutScope(TimeSpan idleTimeout, CancellationToken outerToken)
    {
        _outerToken = outerToken;
        _idleTimeout = idleTimeout;

        if (idleTimeout > TimeSpan.Zero)
        {
            _linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
            _sync = new object();
            Touch();
        }
    }

    public CancellationToken Token => _linkedTokenSource?.Token ?? _outerToken;

    public bool IsTimeoutCancellation =>
        _linkedTokenSource != null &&
        _linkedTokenSource.IsCancellationRequested &&
        !_outerToken.IsCancellationRequested;

    public void Touch()
    {
        if (_linkedTokenSource == null || _sync == null || _idleTimeout <= TimeSpan.Zero) return;

        lock (_sync)
        {
            if (!_linkedTokenSource.IsCancellationRequested)
                _linkedTokenSource.CancelAfter(_idleTimeout);
        }
    }

    public void Dispose()
    {
        _linkedTokenSource?.Dispose();
    }
}
