namespace TinyProxy.Filter;

/// <summary>
/// Filters HTTP headers to protect client privacy.
/// When anonymous mode is enabled, only headers in the AllowedHeaders list are passed through.
/// </summary>
public sealed class AnonymousFilter
{
    private static readonly string[] s_implicitAllowedHeaders = new[]
    {
        "Content-Length",
        "Content-Type"
    };

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

        // Keep content framing headers when explicit allow-list mode is active.
        if (_allowedHeaders.Count > 0)
            foreach (var header in s_implicitAllowedHeaders)
                _allowedHeaders.Add(header);
    }

    /// <summary>
    /// Determines whether header allowed.
    /// </summary>
    public bool IsHeaderAllowed(string headerName)
    {
        return _allowedHeaders.Contains(headerName);
    }
}