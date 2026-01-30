using System.Text.RegularExpressions;

namespace TinyProxy.Config;

/// <summary>
/// Proxy configuration settings.
/// </summary>
public sealed record Configuration
{
    /// <summary>
    /// Gets the listen address.
    /// </summary>
    public string ListenAddress { get; init; } = "127.0.0.1";

    /// <summary>
    /// Gets the listen port.
    /// </summary>
    public ushort ListenPort { get; init; } = 9999;

    /// <summary>
    /// Gets the maximum number of concurrent clients.
    /// </summary>
    public int MaxClients { get; init; } = 100;

    /// <summary>
    /// Gets the maximum number of concurrent connections per IP address.
    /// Set to 0 to disable per-IP limiting.
    /// </summary>
    public int MaxClientsPerIp { get; init; } = 10;

    /// <summary>
    /// Gets the timeout for connections.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the idle timeout for CONNECT tunnels.
    /// </summary>
    public TimeSpan ConnectIdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets the maximum allowed request body size in bytes.
    /// Set to 0 to disable limit (not recommended for production).
    /// </summary>
    public long MaxRequestSize { get; init; } = 10 * 1024 * 1024; // 10 MB default

    /// <summary>
    /// Gets the set of allowed IP addresses.
    /// </summary>
    public HashSet<string> AllowIPs { get; init; } = new();

    /// <summary>
    /// Gets the set of denied IP addresses.
    /// </summary>
    public HashSet<string> DenyIPs { get; init; } = new();

    /// <summary>
    /// Gets the filter regexes for URL filtering.
    /// </summary>
    public List<Regex> FilterRegexes { get; init; } = new();

    /// <summary>
    /// Gets the allowed CONNECT ports.
    /// </summary>
    public HashSet<ushort> AllowedConnectPorts { get; init; } = new() { 443 };

    /// <summary>
    /// Gets the upstream proxy configuration.
    /// </summary>
    public UpstreamProxyConfig? UpstreamProxy { get; init; }

    /// <summary>
    /// Gets the log file path.
    /// </summary>
    public string? LogFile { get; init; }

    /// <summary>
    /// Gets the basic authentication configuration.
    /// </summary>
    public BasicAuthConfig? BasicAuth { get; init; }

    /// <summary>
    /// Gets whether to add Via header.
    /// </summary>
    public bool AddViaHeader { get; init; } = true;

    /// <summary>
    /// Gets whether to add X-Tinyproxy header.
    /// </summary>
    public bool AddXTinyproxyHeader { get; init; } = false;

    /// <summary>
    /// Gets whether to filter URLs by default (deny all unless allowed).
    /// </summary>
    public bool FilterDefaultDeny { get; init; } = false;

    /// <summary>
    /// Gets whether to enable verbose logging.
    /// </summary>
    public bool Verbose { get; init; } = true;

    /// <summary>
    /// Gets the allowed headers for anonymous mode.
    /// When non-empty, only these headers are allowed to pass through to the server.
    /// Aligns with tinyproxy C's anonymous_map.
    /// </summary>
    public HashSet<string> AnonymousAllowedHeaders { get; init; } = new();

    /// <summary>
    /// Gets whether anonymous mode is enabled.
    /// Aligns with tinyproxy C's is_anonymous_enabled().
    /// </summary>
    public bool IsAnonymousEnabled => AnonymousAllowedHeaders.Count > 0;

    /// <summary>
    /// Creates a default configuration.
    /// </summary>
    public static Configuration Default => new();
}

/// <summary>
/// Upstream proxy configuration.
/// </summary>
public sealed record UpstreamProxyConfig
{
    public required string Host { get; init; }
    public required ushort Port { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
}

/// <summary>
/// Basic authentication configuration.
/// </summary>
public sealed record BasicAuthConfig
{
    public required string Username { get; init; }
    public required string Password { get; init; }
    public string? Realm { get; init; } = "TinyProxy";
}
