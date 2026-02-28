namespace TinyProxy.Protocol.Http;

/// <summary>
/// Parsed HTTP request with zero-copy header references.
/// </summary>
public sealed class HttpRequest
{
    public HttpMethod Method { get; init; }
    public string? RawMethod { get; init; }
    public string Uri { get; init; } = string.Empty;
    public string Version { get; init; } = "HTTP/1.1";
    public Dictionary<string, ReadOnlySequence<byte>> Headers { get; init; } = new();
    public IReadOnlyList<KeyValuePair<string, ReadOnlySequence<byte>>> HeaderLines { get; init; } =
        Array.Empty<KeyValuePair<string, ReadOnlySequence<byte>>>();
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
            return TryParseHttpUri(Uri, 80, out host, out port, out _);

        if (Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return TryParseHttpUri(Uri, 443, out host, out port, out _);

        // Relative URI - use Host header
        if (!string.IsNullOrEmpty(Host)) return TextUtils.TryParseHostPort(Host, 80, out host, out port);

        return false;
    }

    private static bool TryParseHttpUri(string uri, int defaultPort, out string host, out int port, out string path)
    {
        host = string.Empty;
        port = defaultPort;
        path = "/";

        // Skip protocol
        var afterProto = uri.Contains("://") ? uri.Substring(uri.IndexOf("://") + 3) : uri;

        // Find path separator
        var slashIndex = afterProto.IndexOf('/');
        if (slashIndex < 0) slashIndex = afterProto.Length;

        var authorityPart = afterProto.Substring(0, slashIndex);
        path = slashIndex < afterProto.Length ? afterProto.Substring(slashIndex) : "/";

        // tinyproxy C strips user:pass@ before host/port parsing.
        var atIndex = authorityPart.LastIndexOf('@');
        if (atIndex >= 0 && atIndex + 1 < authorityPart.Length)
            authorityPart = authorityPart[(atIndex + 1)..];

        return TextUtils.TryParseHostPort(authorityPart, defaultPort, out host, out port);
    }

    public string GetHeader(string name)
    {
        if (Headers.TryGetValue(name, out var value))
        {
            var data = value.Length > 4096
                ? value.Slice(0, 4096)
                : value;

            var span = data.IsSingleSegment ? data.FirstSpan : data.ToArray();
            return Encoding.ASCII.GetString(span);
        }
        return string.Empty;
    }

    public bool HasHeader(string name)
    {
        return Headers.ContainsKey(name);
    }

    public string GetMethodToken()
    {
        if (!string.IsNullOrWhiteSpace(RawMethod)) return RawMethod!;
        return HttpMethodParser.ToHttpString(Method);
    }

    /// <summary>
    /// Creates a copy of the request with a modified URI.
    /// </summary>
    public HttpRequest WithUri(string newUri)
    {
        return new HttpRequest
        {
            Method = Method,
            RawMethod = RawMethod,
            Uri = newUri,
            Version = Version,
            Headers = Headers,
            HeaderLines = HeaderLines,
            Body = Body,
            Host = Host,
            UserAgent = UserAgent,
            ContentType = ContentType,
            ContentLength = ContentLength
        };
    }

    /// <summary>
    /// Creates a copy of the request with a modified body.
    /// </summary>
    public HttpRequest WithBody(ReadOnlySequence<byte> newBody)
    {
        return new HttpRequest
        {
            Method = Method,
            RawMethod = RawMethod,
            Uri = Uri,
            Version = Version,
            Headers = Headers,
            HeaderLines = HeaderLines,
            Body = newBody,
            Host = Host,
            UserAgent = UserAgent,
            ContentType = ContentType,
            ContentLength = ContentLength
        };
    }
}
