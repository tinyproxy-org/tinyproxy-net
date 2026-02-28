using System;
using System.Collections.Generic;
using TinyProxy.Core;

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
    public ushort ListenPort { get; init; } = ProxyConstants.DefaultPort;

    /// <summary>
    /// Gets the maximum number of concurrent clients.
    /// </summary>
    public int MaxClients { get; init; } = ProxyConstants.DefaultMaxClients;

    /// <summary>
    /// Gets the maximum number of concurrent connections per IP address.
    /// Set to 0 to disable per-IP limiting.
    /// </summary>
    public int MaxClientsPerIp { get; init; } = ProxyConstants.DefaultMaxClientsPerIp;

    /// <summary>
    /// Gets the timeout for connections.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(ProxyConstants.DefaultConnectionTimeoutSeconds);

    /// <summary>
    /// Gets the idle timeout for CONNECT tunnels.
    /// </summary>
    public TimeSpan ConnectIdleTimeout { get; init; } = TimeSpan.FromSeconds(ProxyConstants.DefaultConnectIdleTimeoutSeconds);

    /// <summary>
    /// Gets the maximum allowed request body size in bytes.
    /// Set to 0 to disable limit (not recommended for production).
    /// </summary>
    public long MaxRequestSize { get; init; } = ProxyConstants.DefaultMaxRequestSize;

    /// <summary>
    /// Gets the set of allowed IP addresses/patterns.
    /// Supports: IP, CIDR (e.g., 192.168.1.0/24), wildcard (e.g., 192.168.*.*), domain suffix (.example.com).
    /// Aligns with tinyproxy C's ACL functionality.
    /// </summary>
    public HashSet<string> AllowIPs { get; init; } = new();

    /// <summary>
    /// Gets the set of denied IP addresses/patterns.
    /// Supports: IP, CIDR, wildcard, domain suffix.
    /// Aligns with tinyproxy C's ACL functionality.
    /// </summary>
    public HashSet<string> DenyIPs { get; init; } = new();

    /// <summary>
    /// Gets the filter patterns for URL filtering.
    /// Supports both regex and glob patterns (*, ?).
    /// Aligns with tinyproxy C's filter.c.
    /// </summary>
    public List<string> FilterPatterns { get; init; } = new();

    /// <summary>
    /// Gets the path to the filter file containing URL patterns.
    /// When set, filter patterns are loaded from this file.
    /// The file is watched for changes and automatically reloaded.
    /// Aligns with tinyproxy C's Filter option.
    /// </summary>
    public string? FilterFile { get; init; }

    /// <summary>
    /// Gets whether filter patterns are case-sensitive.
    /// Aligns with tinyproxy C's FILTER_OPT_CASESENSITIVE.
    /// </summary>
    public bool FilterCaseSensitive { get; init; } = false;

    /// <summary>
    /// Gets whether to use glob pattern matching instead of regex.
    /// Aligns with tinyproxy C's FILTER_OPT_TYPE_FNMATCH.
    /// </summary>
    public bool FilterUseGlob { get; init; } = false;

    /// <summary>
    /// Gets whether filtering matches full URL (true) or only host/domain (false).
    /// Aligns with tinyproxy C's FILTER_OPT_URL / FilterURLs directive.
    /// </summary>
    public bool FilterUrls { get; init; } = false;

    /// <summary>
    /// Gets the allowed CONNECT ports.
    /// If empty, all ports are allowed.
    /// Aligns with tinyproxy C's connect-ports.c.
    /// </summary>
    public HashSet<ushort> AllowedConnectPorts { get; init; } = new();

    /// <summary>
    /// Gets the upstream proxy configuration.
    /// Aligns with tinyproxy C's upstream.c.
    /// </summary>
    public UpstreamProxyConfig? UpstreamProxy { get; init; }

    /// <summary>
    /// Gets the log file path.
    /// </summary>
    public string? LogFile { get; init; }

    /// <summary>
    /// Gets the basic authentication configuration (single user).
    /// For multiple users, use BasicAuthUsers instead.
    /// </summary>
    public BasicAuthConfig? BasicAuth { get; init; }

    /// <summary>
    /// Gets the list of basic authentication users.
    /// Supports multiple user credentials.
    /// </summary>
    public List<BasicAuthUser> BasicAuthUsers { get; init; } = new();

    /// <summary>
    /// Gets whether to add Via header.
    /// Aligns with tinyproxy C's Via header handling in reqs.c.
    /// </summary>
    public bool AddViaHeader { get; init; } = true;

    /// <summary>
    /// Gets the custom proxy name for Via header.
    /// If null, uses system hostname (aligns with tinyproxy C).
    /// </summary>
    public string? ViaProxyName { get; init; }

    /// <summary>
    /// Gets whether to add X-Tinyproxy header.
    /// </summary>
    public bool AddXTinyproxyHeader { get; init; } = false;

    /// <summary>
    /// Gets whether to filter URLs by default (deny all unless allowed).
    /// Aligns with tinyproxy C's FILTER_OPT_DEFAULT_DENY.
    /// </summary>
    public bool FilterDefaultDeny { get; init; } = false;

    /// <summary>
    /// Gets whether to enable verbose logging.
    /// </summary>
    public bool Verbose { get; init; } = true;

    /// <summary>
    /// Gets the allowed headers for anonymous mode.
    /// When non-empty, only these headers are allowed to pass through to the server.
    /// Aligns with tinyproxy C's anonymous.c.
    /// </summary>
    public HashSet<string> AnonymousAllowedHeaders { get; init; } = new();

    /// <summary>
    /// Gets whether anonymous mode is enabled.
    /// Aligns with tinyproxy C's is_anonymous_enabled().
    /// </summary>
    public bool IsAnonymousEnabled => AnonymousAllowedHeaders.Count > 0;

    /// <summary>
    /// Gets whether transparent proxy mode is enabled.
    /// When enabled, the proxy operates in transparent mode where client requests
    /// are redirected by firewall rules (iptables, pf, etc.) without client configuration.
    /// The proxy determines the original destination using getsockname().
    /// Aligns with tinyproxy C's TRANSPARENT_PROXY.
    /// </summary>
    public bool IsTransparentProxyEnabled { get; init; } = false;

    /// <summary>
    /// Gets whether reverse proxy mode is enabled.
    /// Aligns with tinyproxy C's REVERSE_SUPPORT.
    /// </summary>
    public bool IsReverseProxyEnabled { get; init; } = false;

    /// <summary>
    /// Gets the reverse proxy path mappings.
    /// Maps local paths to upstream URLs.
    /// Aligns with tinyproxy C's reversepath_list.
    /// </summary>
    public List<ReversePathConfig> ReversePaths { get; init; } = new();

    /// <summary>
    /// Gets whether reverse proxy "magic" cookie tracking is enabled.
    /// When enabled, the proxy uses a special cookie to track which reverse path
    /// a client is using.
    /// Aligns with tinyproxy C's reversemagic.
    /// </summary>
    public bool ReverseMagicEnabled { get; init; } = false;

    /// <summary>
    /// Gets the reverse base URL for rewriting redirects.
    /// Aligns with tinyproxy C's reversebaseurl.
    /// </summary>
    public string? ReverseBaseUrl { get; init; }

    /// <summary>
    /// Gets the addresses to bind outgoing connections to.
    /// When set, outgoing connections to servers will use these source addresses.
    /// Aligns with tinyproxy C's bind_addrs / BindSame.
    /// </summary>
    public HashSet<string> BindAddresses { get; init; } = new();

    /// <summary>
    /// Gets whether to bind outgoing connections to the incoming interface IP.
    /// Aligns with tinyproxy C's bindsame.
    /// </summary>
    public bool BindSame { get; init; } = false;

    /// <summary>
    /// Gets the statistics page host.
    /// When accessed, shows runtime statistics.
    /// Aligns with tinyproxy C's statpage.
    /// </summary>
    public string? StatHost { get; init; }

    /// <summary>
    /// Gets the PID file path for writing the process ID.
    /// Aligns with tinyproxy C's pidfile.
    /// </summary>
    public string? PidFile { get; init; }

    /// <summary>
    /// Gets whether to use syslog for logging.
    /// Aligns with tinyproxy C's syslog.
    /// </summary>
    public bool UseSyslog { get; init; } = false;

    /// <summary>
    /// Gets the syslog server address.
    /// Aligns with tinyproxy C's syslog configuration.
    /// </summary>
    public string? SyslogServer { get; init; }

    /// <summary>
    /// Gets the syslog server port.
    /// Default is 514 (standard syslog port).
    /// </summary>
    public int SyslogPort { get; init; } = ProxyConstants.DefaultSyslogPort;

    /// <summary>
    /// Gets directory path for custom error pages.
    /// When set, error pages are loaded from this directory.
    /// Aligns with tinyproxy C's ErrorFile directive.
    /// </summary>
    public string? ErrorPagesDirectory { get; init; }

    /// <summary>
    /// Gets custom error page mappings by status code.
    /// Key is HTTP status code, value is file path to custom error page.
    /// Aligns with tinyproxy C's html-error.c.
    /// </summary>
    public Dictionary<int, string> CustomErrorPages { get; init; } = new();

    /// <summary>
    /// Gets custom headers to add to all outgoing requests.
    /// Aligns with tinyproxy C's AddHeader directive.
    /// </summary>
    public List<HttpHeader> CustomHeaders { get; init; } = new();

    /// <summary>
    /// Creates a default configuration.
    /// </summary>
    public static Configuration Default => new();
}

/// <summary>
/// Represents a custom HTTP header.
/// </summary>
public sealed record HttpHeader
{
    public required string Name { get; init; }
    public required string Value { get; init; }
}

/// <summary>
/// Reverse proxy path configuration.
/// Aligns with tinyproxy C's reversepath struct.
/// </summary>
public sealed record ReversePathConfig
{
    /// <summary>
    /// Gets the local path prefix (e.g., "/app").
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets the upstream URL to map to (e.g., "http://backend:8080").
    /// </summary>
    public required string Url { get; init; }
}

/// <summary>
/// Upstream proxy configuration.
/// Supports HTTP, SOCKS4, and SOCKS5 proxies.
/// Aligns with tinyproxy C's upstream struct with proxy_type.
/// </summary>
public sealed record UpstreamProxyConfig
{
    public required string Host { get; init; }
    public required ushort Port { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }

    /// <summary>
    /// Gets the upstream proxy type.
    /// Aligns with tinyproxy C's proxy_type enum.
    /// </summary>
    public UpstreamProxyType Type { get; init; } = UpstreamProxyType.Http;

    /// <summary>
    /// Gets the domain pattern for matching requests to this upstream proxy.
    /// When null, this is the default upstream proxy for all requests.
    /// Aligns with tinyproxy C's upstream->target (hostspec).
    /// </summary>
    public string? Domain { get; init; }
}

/// <summary>
/// Upstream proxy type.
/// Aligns with tinyproxy C's proxy_type enum.
/// </summary>
public enum UpstreamProxyType
{
    /// <summary>
    /// No proxy (direct connection).
    /// </summary>
    None,

    /// <summary>
    /// HTTP proxy.
    /// </summary>
    Http,

    /// <summary>
    /// SOCKS4 proxy.
    /// </summary>
    Socks4,

    /// <summary>
    /// SOCKS5 proxy.
    /// </summary>
    Socks5
}

/// <summary>
/// Basic authentication configuration (single user).
/// </summary>
public sealed record BasicAuthConfig
{
    public required string Username { get; init; }
    public required string Password { get; init; }
    public string? Realm { get; init; } = "TinyProxy";
}

/// <summary>
/// Basic authentication user configuration.
/// Supports multiple users.
/// </summary>
public sealed record BasicAuthUser
{
    public required string Username { get; init; }
    public required string Password { get; init; }
}
