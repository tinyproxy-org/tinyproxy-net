namespace TinyProxy.Core;

/// <summary>
/// Simple object pool for reusing expensive-to-create objects.
/// </summary>
/// <typeparam name="T">The type of object to pool.</typeparam>
public sealed class ObjectPool<T> where T : class
{
    private readonly T[] _items;
    private readonly Func<T> _factory;
    private int _count;

    public ObjectPool(int capacity, Func<T> factory)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _items = new T[capacity];
    }

    /// <summary>
    /// Gets an object from the pool or creates a new one.
    /// </summary>
    public T Rent()
    {
        if (_count > 0)
        {
            var index = --_count;
            var item = _items[index];
            _items[index] = null!;
            return item;
        }

        return _factory();
    }

    /// <summary>
    /// Returns an object to the pool for reuse.
    /// </summary>
    public void Return(T item)
    {
        if (item == null) return;

        // Only keep items if pool isn't full
        if (_count < _items.Length)
        {
            _items[_count++] = item;
        }
    }
}
