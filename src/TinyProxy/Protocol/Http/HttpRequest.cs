namespace TinyProxy.Protocol.Http;

/// <summary>
/// Parsed HTTP request with zero-copy header references.
/// </summary>
public sealed class HttpRequest
{
    /// <summary>
    /// Gets or sets method.
    /// </summary>
    public HttpMethod Method { get; init; }
    /// <summary>
    /// Gets or sets raw method.
    /// </summary>
    public string? RawMethod { get; init; }
    /// <summary>
    /// Gets or sets uri.
    /// </summary>
    public string Uri { get; init; } = string.Empty;
    /// <summary>
    /// Gets or sets version.
    /// </summary>
    public string Version { get; init; } = "HTTP/1.1";

    /// <summary>
    /// Gets or sets request headers indexed by name.
    /// </summary>
    public Dictionary<string, ReadOnlySequence<byte>> Headers { get; init; } = new();

    /// <summary>
    /// Gets or sets header lines.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, ReadOnlySequence<byte>>> HeaderLines { get; init; } =
        Array.Empty<KeyValuePair<string, ReadOnlySequence<byte>>>();

    /// <summary>
    /// Gets or sets body.
    /// </summary>
    public ReadOnlySequence<byte> Body { get; init; }

    /// <summary>
    /// Gets or sets host.
    /// </summary>
    public string? Host { get; init; }
    /// <summary>
    /// Gets or sets user agent.
    /// </summary>
    public string? UserAgent { get; init; }
    /// <summary>
    /// Gets or sets content type.
    /// </summary>
    public string? ContentType { get; init; }
    /// <summary>
    /// Gets or sets content length.
    /// </summary>
    public long? ContentLength { get; init; }
    /// <summary>
    /// Gets or sets reverse magic cookie path.
    /// </summary>
    public string? ReverseMagicCookiePath { get; init; }

    /// <summary>
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

        if (!string.IsNullOrEmpty(Host)) return TextUtils.TryParseHostPort(Host, 80, out host, out port);

        return false;
    }

    private static bool TryParseHttpUri(string uri, int defaultPort, out string host, out int port, out string path)
    {
        host = string.Empty;
        port = defaultPort;
        path = "/";

        var afterProto = uri.Contains("://") ? uri.Substring(uri.IndexOf("://") + 3) : uri;

        var slashIndex = afterProto.IndexOf('/');
        if (slashIndex < 0) slashIndex = afterProto.Length;

        var authorityPart = afterProto.Substring(0, slashIndex);
        path = slashIndex < afterProto.Length ? afterProto.Substring(slashIndex) : "/";

        return TextUtils.TryParseHostPort(authorityPart, defaultPort, out host, out port);
    }

    /// <summary>
    /// Gets header.
    /// </summary>
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

    /// <summary>
    /// Determines whether this instance has header.
    /// </summary>
    public bool HasHeader(string name)
    {
        return Headers.ContainsKey(name);
    }

    /// <summary>
    /// Gets method token.
    /// </summary>
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
            ContentLength = ContentLength,
            ReverseMagicCookiePath = ReverseMagicCookiePath
        };
    }

    /// <summary>
    /// Returns a copy with reverse magic cookie path.
    /// </summary>
    public HttpRequest WithReverseMagicCookiePath(string reverseMagicCookiePath)
    {
        return new HttpRequest
        {
            Method = Method,
            RawMethod = RawMethod,
            Uri = Uri,
            Version = Version,
            Headers = Headers,
            HeaderLines = HeaderLines,
            Body = Body,
            Host = Host,
            UserAgent = UserAgent,
            ContentType = ContentType,
            ContentLength = ContentLength,
            ReverseMagicCookiePath = reverseMagicCookiePath
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
            ContentLength = ContentLength,
            ReverseMagicCookiePath = ReverseMagicCookiePath
        };
    }
}
