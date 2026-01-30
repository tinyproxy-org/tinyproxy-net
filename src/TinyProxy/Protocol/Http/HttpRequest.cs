using System.Buffers;
using System.Text;

namespace TinyProxy.Protocol.Http;

/// <summary>
/// Parsed HTTP request with zero-copy header references.
/// </summary>
public sealed class HttpRequest
{
    public HttpMethod Method { get; init; }
    public string Uri { get; init; } = string.Empty;
    public string Version { get; init; } = "HTTP/1.1";
    public Dictionary<string, ReadOnlySequence<byte>> Headers { get; init; } = new();
    public ReadOnlySequence<byte> Body { get; init; }

    // Common headers - parsed for quick access
    public string? Host { get; init; }
    public string? UserAgent { get; init; }
    public string? ContentType { get; init; }
    public long? ContentLength { get; init; }

    /// <summary>
    /// Gets the target host and port from the request.
    /// For absolute URI (proxy request), extracts from URI.
    /// For relative URI, uses Host header.
    /// </summary>
    public bool TryGetTarget(out string host, out int port)
    {
        host = string.Empty;
        port = 80;

        if (Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseHttpUri(Uri, out host, out port, out _);
        }

        if (Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseHttpUri(Uri, out host, out port, out _);
        }

        // Relative URI - use Host header
        if (!string.IsNullOrEmpty(Host))
        {
            return TryParseHostHeader(Host, out host, out port);
        }

        return false;
    }

    private static bool TryParseHttpUri(string uri, out string host, out int port, out string path)
    {
        host = string.Empty;
        port = 80;
        path = "/";

        // Skip protocol
        var afterProto = uri.Contains("://") ? uri.Substring(uri.IndexOf("://") + 3) : uri;

        // Find path separator
        var slashIndex = afterProto.IndexOf('/');
        if (slashIndex < 0)
        {
            slashIndex = afterProto.Length;
        }

        var authorityPart = afterProto.Substring(0, slashIndex);
        path = slashIndex < afterProto.Length ? afterProto.Substring(slashIndex) : "/";

        return TryParseHostHeader(authorityPart, out host, out port);
    }

    private static bool TryParseHostHeader(string authority, out string host, out int port)
    {
        host = string.Empty;
        port = 80;

        // Find IPv6 brackets or colon
        var bracketStart = authority.IndexOf('[');
        if (bracketStart >= 0)
        {
            // IPv6 address [::1]:port
            var bracketEnd = authority.IndexOf(']', bracketStart);
            if (bracketEnd < 0) return false;

            host = authority.Substring(bracketStart + 1, bracketEnd - bracketStart - 1);

            if (bracketEnd + 1 < authority.Length && authority[bracketEnd + 1] == ':')
            {
                _ = int.TryParse(authority.Substring(bracketEnd + 2), out port);
            }
        }
        else
        {
            // IPv4 or hostname
            var colonIndex = authority.IndexOf(':');
            if (colonIndex >= 0)
            {
                host = authority.Substring(0, colonIndex);
                _ = int.TryParse(authority.Substring(colonIndex + 1), out port);
            }
            else
            {
                host = authority;
            }
        }

        return !string.IsNullOrEmpty(host);
    }

    public string GetHeader(string name)
    {
        if (Headers.TryGetValue(name, out var value))
        {
            return value.Length > 4096
                ? value.Slice(0, 4096).ToString() // Truncate large headers
                : value.ToString();
        }
        return string.Empty;
    }

    public bool HasHeader(string name) => Headers.ContainsKey(name);

    /// <summary>
    /// Creates a copy of the request with a modified URI.
    /// </summary>
    public HttpRequest WithUri(string newUri) => new()
    {
        Method = Method,
        Uri = newUri,
        Version = Version,
        Headers = Headers,
        Body = Body,
        Host = Host,
        UserAgent = UserAgent,
        ContentType = ContentType,
        ContentLength = ContentLength
    };
}
