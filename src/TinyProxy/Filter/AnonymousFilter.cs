using System;
using System.Collections.Generic;

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
    /// Checks if a header is allowed to pass through.
    /// Aligns with tinyproxy C's anonymous_search().
    /// </summary>
    public bool IsHeaderAllowed(string headerName)
    {
        return _allowedHeaders.Contains(headerName);
    }
}