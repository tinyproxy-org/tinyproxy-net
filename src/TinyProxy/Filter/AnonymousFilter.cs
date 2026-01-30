using System.Buffers;

namespace TinyProxy.Filter;

/// <summary>
/// Filters HTTP headers to protect client privacy.
/// Aligns with tinyproxy C's anonymous.c functionality.
/// When anonymous mode is enabled, only headers in the AllowedHeaders list are passed through.
/// </summary>
public sealed class AnonymousFilter
{
    private readonly HashSet<string> _allowedHeaders;

    /// <summary>
    /// Creates a new anonymous filter with no allowed headers (deny all mode).
    /// </summary>
    public AnonymousFilter()
    {
        _allowedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates a new anonymous filter with the specified allowed headers.
    /// </summary>
    public AnonymousFilter(IEnumerable<string> allowedHeaders)
    {
        _allowedHeaders = new HashSet<string>(allowedHeaders, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Adds a header to the allowed list.
    /// Aligns with tinyproxy C's anonymous_insert().
    /// </summary>
    public void AllowHeader(string headerName)
    {
        _allowedHeaders.Add(headerName);
    }

    /// <summary>
    /// Checks if a header is allowed to pass through.
    /// Aligns with tinyproxy C's anonymous_search().
    /// </summary>
    public bool IsHeaderAllowed(string headerName)
    {
        return _allowedHeaders.Contains(headerName);
    }

    /// <summary>
    /// Checks if anonymous filtering is enabled (has any allowed headers configured).
    /// Aligns with tinyproxy C's is_anonymous_enabled().
    /// </summary>
    public bool IsEnabled => _allowedHeaders.Count > 0;

    /// <summary>
    /// Filters headers, returning only those that are allowed.
    /// </summary>
    public IEnumerable<KeyValuePair<string, ReadOnlySequence<byte>>> FilterHeaders(
        IDictionary<string, ReadOnlySequence<byte>> headers)
    {
        if (!IsEnabled)
        {
            return headers;
        }

        return headers.Where(h => _allowedHeaders.Contains(h.Key));
    }

    /// <summary>
    /// Gets the default headers that are safe to pass through in anonymous mode.
    /// These are headers required for the proxy to function correctly.
    /// </summary>
    public static HashSet<string> GetDefaultAllowedHeaders()
    {
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Host",
            "Content-Type",
            "Content-Length",
            "Accept",
            "Accept-Encoding",
            "Accept-Language",
            "Cookie",
            "Authorization",
            "Range",
            "If-Range",
            "If-Modified-Since",
            "If-None-Match",
            "If-Unmodified-Since",
            "Cache-Control"
        };
    }
}
