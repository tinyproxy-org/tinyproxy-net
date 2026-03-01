namespace TinyProxy.Core;

/// <summary>
/// Cache for StringBuilder instances to reduce allocations in hot paths.
/// Uses async-local storage for thread safety without locks.
/// </summary>
public static class StringBuilderCache
{
    private const int DefaultCapacity = 256;
    private const int MaxBuilderSize = 3600; // Max size before we let GC handle it

    private static readonly AsyncLocal<StringBuilderCacheEntry?> _cache = new();

    /// <summary>
    /// Acquires a StringBuilder from the cache or creates a new one.
    /// </summary>
    public static StringBuilder Acquire()
    {
        var entry = _cache.Value;
        if (entry != null && entry.Builder != null)
        {
            _cache.Value = null;
            entry.Builder.Clear();
            return entry.Builder;
        }

        return new StringBuilder(DefaultCapacity);
    }

    /// <summary>
    /// Acquires a StringBuilder with minimum capacity.
    /// </summary>
    public static StringBuilder Acquire(int capacity)
    {
        var entry = _cache.Value;
        if (entry != null && entry.Builder != null)
        {
            _cache.Value = null;
            entry.Builder.Clear();
            if (entry.Builder.Capacity < capacity) entry.Builder.Capacity = capacity;
            return entry.Builder;
        }

        return new StringBuilder(capacity);
    }

    /// <summary>
    /// Releases a StringBuilder back to the cache if it's not too large.
    /// </summary>
    public static void Release(StringBuilder sb)
    {
        if (sb == null || sb.Capacity > MaxBuilderSize) return;

        _cache.Value = new StringBuilderCacheEntry { Builder = sb };
    }

    /// <summary>
    /// Gets string and release.
    /// </summary>
    public static string GetStringAndRelease(StringBuilder sb)
    {
        var result = sb.ToString();
        Release(sb);
        return result;
    }

    private class StringBuilderCacheEntry
    {
        public StringBuilder? Builder;
    }
}