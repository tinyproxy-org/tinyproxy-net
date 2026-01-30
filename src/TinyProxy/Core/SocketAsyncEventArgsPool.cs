using System.Buffers;
using System.Net.Sockets;

namespace TinyProxy.Core;

/// <summary>
/// Pool for SocketAsyncEventArgs to reduce allocations in high-throughput scenarios.
/// Aligns with .NET best practices for high-performance socket operations.
/// </summary>
public sealed class SocketAsyncEventArgsPool : IDisposable
{
    private readonly SocketAsyncEventArgs[] _pool;
    private readonly int _maxPoolSize;
    private int _poolIndex;
    private bool _disposed;

    public SocketAsyncEventArgsPool(int maxPoolSize = 256)
    {
        if (maxPoolSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPoolSize));

        _maxPoolSize = maxPoolSize;
        _pool = new SocketAsyncEventArgs[maxPoolSize];
        _poolIndex = 0;

        // Pre-allocate buffer for each SAEA
        for (int i = 0; i < maxPoolSize; i++)
        {
            _pool[i] = new SocketAsyncEventArgs();
            _pool[i].SetBuffer(ArrayPool<byte>.Shared.Rent(8192));
        }
    }

    /// <summary>
    /// Rents a SocketAsyncEventArgs from the pool.
    /// </summary>
    public SocketAsyncEventArgs Rent()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SocketAsyncEventArgsPool));

        if (_poolIndex < _maxPoolSize)
        {
            var item = _pool[_poolIndex];
            _poolIndex++;
            return item;
        }

        // Pool exhausted, create new
        var saea = new SocketAsyncEventArgs();
        saea.SetBuffer(ArrayPool<byte>.Shared.Rent(8192));
        return saea;
    }

    /// <summary>
    /// Returns a SocketAsyncEventArgs to the pool for reuse.
    /// </summary>
    public void Return(SocketAsyncEventArgs saea)
    {
        if (_disposed) return;

        // Reset UserToken only (Socket and RemoteEndPoint are set per-use)
        saea.UserToken = null;

        // If it's from our original pool, decrement index
        // (This is a simple implementation - for production you'd want better tracking)
        if (_poolIndex > 0)
        {
            _poolIndex--;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        for (int i = 0; i < _maxPoolSize; i++)
        {
            if (_pool[i] != null)
            {
                _pool[i].Dispose();
                _pool[i] = null!;
            }
        }
    }
}
