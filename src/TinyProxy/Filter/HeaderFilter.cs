using System.Buffers;
using System.Text;

namespace TinyProxy.Filter;

/// <summary>
/// HTTP header modification and filtering.
/// </summary>
public sealed class HeaderFilter
{
    private readonly HashSet<string> _headersToRemove;
    private readonly Dictionary<string, string> _headersToAdd;
    private readonly Dictionary<string, string> _headersToReplace;

    public HeaderFilter()
    {
        _headersToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _headersToAdd = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _headersToReplace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Adds a header to the remove list.
    /// </summary>
    public void RemoveHeader(string name)
    {
        _headersToRemove.Add(name);
    }

    /// <summary>
    /// Adds a header to add to all requests.
    /// </summary>
    public void AddHeader(string name, string value)
    {
        _headersToAdd[name] = value;
    }

    /// <summary>
    /// Adds a header to replace existing values.
    /// </summary>
    public void ReplaceHeader(string name, string value)
    {
        _headersToReplace[name] = value;
    }

    /// <summary>
    /// Filters and modifies headers for forwarding.
    /// </summary>
    public IEnumerable<(string name, ReadOnlySequence<byte> value)> FilterHeaders(
        IDictionary<string, ReadOnlySequence<byte>> headers)
    {
        foreach (var header in headers)
        {
            var name = header.Key;

            // Skip removed headers
            if (_headersToRemove.Contains(name))
            {
                continue;
            }

            // Use replacement value if configured
            if (_headersToReplace.TryGetValue(name, out var replacement))
            {
                var replacementBytes = Encoding.ASCII.GetBytes(replacement);
                yield return (name, new ReadOnlySequence<byte>(replacementBytes));
                continue;
            }

            // Pass through original header
            yield return (name, header.Value);
        }

        // Add additional headers
        foreach (var header in _headersToAdd)
        {
            // Don't add if it already exists in original headers
            if (!headers.ContainsKey(header.Key))
            {
                var valueBytes = Encoding.ASCII.GetBytes(header.Value);
                yield return (header.Key, new ReadOnlySequence<byte>(valueBytes));
            }
        }
    }

    /// <summary>
    /// Gets the default hop-by-hop headers to remove.
    /// Aligns with RFC 2616 Section 13.5.1 and RFC 7230 Section 6.1.
    /// </summary>
    public static HashSet<string> GetHopByHopHeaders()
    {
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Connection",
            "Keep-Alive",
            "Proxy-Authenticate",
            "Proxy-Authorization",
            "TE",
            "Trailers",
            "Transfer-Encoding",
            "Upgrade"  // RFC 7230: hop-by-hop header for protocol upgrades
        };
    }
}
