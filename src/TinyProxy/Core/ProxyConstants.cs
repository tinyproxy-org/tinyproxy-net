namespace TinyProxy.Core;

/// <summary>
/// Constants used throughout the proxy implementation.
/// Centralizes magic numbers and configuration values.
/// </summary>
public static class ProxyConstants
{
    /// <summary>
    /// Default buffer size for socket operations.
    /// 64KB provides a good balance between memory usage and throughput.
    /// </summary>
    public const int DefaultBufferSize = 65536;

    /// <summary>
    /// Initial buffer size for reading HTTP headers.
    /// 8KB is sufficient for most HTTP requests.
    /// </summary>
    public const int InitialHeaderBufferSize = 8192;

    /// <summary>
    /// Maximum size for HTTP headers.
    /// 64KB matches typical server limits and prevents header overflow attacks.
    /// </summary>
    public const int MaxHeaderSize = 65536;

    /// <summary>
    /// Default maximum request body size (10MB).
    /// </summary>
    public const long DefaultMaxRequestSize = 10 * 1024 * 1024;

    /// <summary>
    /// Maximum DNS cache size to prevent unbounded memory growth.
    /// </summary>
    public const int MaxDnsCacheSize = 1000;

    /// <summary>
    /// Maximum number of headers to parse in a single request.
    /// Aligns with tinyproxy C's MAX_HEADERS in reqs.c.
    /// </summary>
    public const int MaxHeaders = 10000;

    /// <summary>
    /// Maximum URL length allowed.
    /// Aligns with typical web server limits.
    /// </summary>
    public const int MaxUrlLength = 2048;

    /// <summary>
    /// Chunk size for streaming large responses.
    /// </summary>
    public const int StreamBufferSize = 16384;

    /// <summary>
    /// Yield threshold for cooperative scheduling.
    /// After receiving this many bytes, yield to allow fairness.
    /// </summary>
    public const int YieldThreshold = 32768;

    /// <summary>
    /// Default connection timeout in seconds.
    /// </summary>
    public const int DefaultConnectionTimeoutSeconds = 30;

    /// <summary>
    /// Default idle timeout for CONNECT tunnels in seconds.
    /// </summary>
    public const int DefaultConnectIdleTimeoutSeconds = 60;

    /// <summary>
    /// Default listen port.
    /// </summary>
    public const int DefaultPort = 8889;

    /// <summary>
    /// Default maximum concurrent connections.
    /// </summary>
    public const int DefaultMaxClients = 100;

    /// <summary>
    /// Default maximum concurrent connections per IP.
    /// </summary>
    public const int DefaultMaxClientsPerIp = 10;

    /// <summary>
    /// HTTP version string.
    /// </summary>
    public const string HttpVersion = "HTTP/1.1";

    /// <summary>
    /// Default syslog server port (RFC 5424).
    /// </summary>
    public const int DefaultSyslogPort = 514;

    /// <summary>
    /// Carriage return line feed sequence.
    /// </summary>
    public const string Crlf = "\r\n";

    /// <summary>
    /// Carriage return line feed sequence (bytes).
    /// </summary>
    public static readonly ReadOnlyMemory<byte> CrlfBytes = new byte[] { (byte)'\r', (byte)'\n' };

    /// <summary>
    /// Double CRLF marking end of headers.
    /// </summary>
    public static readonly ReadOnlyMemory<byte> HeaderEndBytes = new byte[] { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };

    // Hop-by-hop headers that should not be forwarded.
    // Defined in RFC 2616 Section 13.5.1.
    public static readonly string[] HopByHopHeaders = new[]
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "Proxy-Connection",
        "Te",
        "Trailers",
        "Transfer-Encoding",
        "Upgrade"
    };

    // HashSet version for O(1) lookup
    public static readonly HashSet<string> HopByHopHeadersSet = new(HopByHopHeaders, StringComparer.OrdinalIgnoreCase);

    // Headers that should be filtered in anonymous mode.
    public static readonly string[] AnonymousFilteredHeaders = new[]
    {
        "Cookie",
        "Cookie2",
        "From",
        "Referer",
        "User-Agent",
        "X-Forwarded-For"
    };
}
