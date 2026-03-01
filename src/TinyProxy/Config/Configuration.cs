namespace TinyProxy.Config;

/// <summary>
/// Proxy configuration settings.
/// </summary>
public sealed record Configuration
{
    /// <summary>
    /// Gets or sets listen address.
    /// </summary>
    public string ListenAddress { get; init; } = "127.0.0.1";

    /// <summary>
    /// Gets or sets listen port.
    /// </summary>
    public ushort ListenPort { get; init; } = ProxyConstants.DefaultPort;

    /// <summary>
    /// Gets or sets max clients.
    /// </summary>
    public int MaxClients { get; init; } = ProxyConstants.DefaultMaxClients;

    /// <summary>
    /// Set to 0 to disable per-IP limiting.
    /// </summary>
    public int MaxClientsPerIp { get; init; } = ProxyConstants.DefaultMaxClientsPerIp;

    /// <summary>
    /// Gets or sets request processing timeout.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(ProxyConstants.DefaultConnectionTimeoutSeconds);

    /// <summary>
    /// Gets or sets CONNECT tunnel idle timeout.
    /// </summary>
    public TimeSpan ConnectIdleTimeout { get; init; } = TimeSpan.FromSeconds(ProxyConstants.DefaultConnectIdleTimeoutSeconds);

    /// <summary>
    /// Set to 0 to disable limit (not recommended for production).
    /// </summary>
    public long MaxRequestSize { get; init; } = ProxyConstants.DefaultMaxRequestSize;

    /// <summary>
    /// Supports: IP, CIDR (e.g., 192.168.1.0/24), wildcard (e.g., 192.168.*.*), domain suffix (.example.com).
    /// </summary>
    public HashSet<string> AllowIPs { get; init; } = new();

    /// <summary>
    /// Supports: IP, CIDR, wildcard, domain suffix.
    /// </summary>
    public HashSet<string> DenyIPs { get; init; } = new();

    /// <summary>
    /// Gets or sets ordered ACL rules.
    /// </summary>
    public List<AclRuleConfig> AccessRules { get; init; } = new();

    /// <summary>
    /// Supports both regex and glob patterns (*, ?).
    /// </summary>
    public List<string> FilterPatterns { get; init; } = new();

    /// <summary>
    /// When set, filter patterns are loaded from this file.
    /// The file is watched for changes and automatically reloaded.
    /// </summary>
    public string? FilterFile { get; init; }

    /// <summary>
    /// Gets a value indicating whether filter case sensitive.
    /// </summary>
    public bool FilterCaseSensitive { get; init; } = false;

    /// <summary>
    /// Gets a value indicating whether filter use glob.
    /// </summary>
    public bool FilterUseGlob { get; init; } = false;

    /// <summary>
    /// Gets a value indicating whether filter urls.
    /// </summary>
    public bool FilterUrls { get; init; } = false;

    /// <summary>
    /// If empty, all ports are allowed.
    /// </summary>
    public HashSet<ushort> AllowedConnectPorts { get; init; } = new();

    /// <summary>
    /// Gets or sets upstream proxy.
    /// </summary>
    public UpstreamProxyConfig? UpstreamProxy { get; init; }

    /// <summary>
    /// Rules are evaluated in list order and may include bypass ("none") entries.
    /// </summary>
    public List<UpstreamProxyRuleConfig> UpstreamProxyRules { get; init; } = new();

    /// <summary>
    /// Gets or sets log file.
    /// </summary>
    public string? LogFile { get; init; }

    /// <summary>
    /// For multiple users, use BasicAuthUsers instead.
    /// </summary>
    public BasicAuthConfig? BasicAuth { get; init; }

    /// <summary>
    /// Supports multiple user credentials.
    /// </summary>
    public List<BasicAuthUser> BasicAuthUsers { get; init; } = new();

    /// <summary>
    /// Gets a value indicating whether add via header.
    /// </summary>
    public bool AddViaHeader { get; init; } = true;

    /// <summary>
    /// Gets or sets via proxy name.
    /// </summary>
    public string? ViaProxyName { get; init; }

    /// <summary>
    /// Gets a value indicating whether add x tinyproxy header.
    /// </summary>
    public bool AddXTinyproxyHeader { get; init; } = false;

    /// <summary>
    /// Gets a value indicating whether filter default deny.
    /// </summary>
    public bool FilterDefaultDeny { get; init; } = false;

    /// <summary>
    /// Gets a value indicating whether verbose.
    /// </summary>
    public bool Verbose { get; init; } = true;

    /// <summary>
    /// When non-empty, only these headers are allowed to pass through to the server.
    /// </summary>
    public HashSet<string> AnonymousAllowedHeaders { get; init; } = new();

    /// <summary>
    /// Gets a value indicating whether anonymous enabled.
    /// </summary>
    public bool IsAnonymousEnabled => AnonymousAllowedHeaders.Count > 0;

    /// <summary>
    /// When enabled, the proxy operates in transparent mode where client requests
    /// are redirected by firewall rules (iptables, pf, etc.) without client configuration.
    /// The proxy determines the original destination using getsockname().
    /// </summary>
    public bool IsTransparentProxyEnabled { get; init; } = false;

    /// <summary>
    /// Gets a value indicating whether reverse proxy enabled.
    /// </summary>
    public bool IsReverseProxyEnabled { get; init; } = false;

    /// <summary>
    /// Gets a value indicating whether reverse only.
    /// </summary>
    public bool ReverseOnly { get; init; } = false;

    /// <summary>
    /// Maps local paths to upstream URLs.
    /// </summary>
    public List<ReversePathConfig> ReversePaths { get; init; } = new();

    /// <summary>
    /// When enabled, the proxy uses a special cookie to track which reverse path
    /// a client is using.
    /// </summary>
    public bool ReverseMagicEnabled { get; init; } = false;

    /// <summary>
    /// Gets or sets reverse base url.
    /// </summary>
    public string? ReverseBaseUrl { get; init; }

    /// <summary>
    /// When set, outgoing connections to servers will use these source addresses.
    /// </summary>
    public HashSet<string> BindAddresses { get; init; } = new();

    /// <summary>
    /// Gets a value indicating whether bind same.
    /// </summary>
    public bool BindSame { get; init; } = false;

    /// <summary>
    /// When accessed, shows runtime statistics.
    /// </summary>
    public string? StatHost { get; init; }

    /// <summary>
    /// Gets or sets pid file.
    /// </summary>
    public string? PidFile { get; init; }

    /// <summary>
    /// Gets a value indicating whether use syslog.
    /// </summary>
    public bool UseSyslog { get; init; } = false;

    /// <summary>
    /// Gets or sets syslog server.
    /// </summary>
    public string? SyslogServer { get; init; }

    /// <summary>
    /// Default is 514 (standard syslog port).
    /// </summary>
    public int SyslogPort { get; init; } = ProxyConstants.DefaultSyslogPort;

    /// <summary>
    /// When set, error pages are loaded from this directory.
    /// </summary>
    public string? ErrorPagesDirectory { get; init; }

    /// <summary>
    /// Key is HTTP status code, value is file path to custom error page.
    /// </summary>
    public Dictionary<int, string> CustomErrorPages { get; init; } = new();

    /// <summary>
    /// Gets or sets custom headers appended to proxied requests.
    /// </summary>
    public List<HttpHeader> CustomHeaders { get; init; } = new();

    /// <summary>
    /// Gets a value indicating whether this instance has upstream proxy configured.
    /// </summary>
    public bool HasUpstreamProxyConfigured => UpstreamProxyRules.Count > 0 || UpstreamProxy != null;

    /// <summary>
    /// Resolves the effective upstream proxy for a target host.
    /// Returns null when traffic should bypass upstream (including "none" rules).
    /// </summary>
    public UpstreamProxyConfig? ResolveUpstreamProxy(string targetHost)
    {
        if (UpstreamProxyRules.Count == 0) return UpstreamProxy;

        var normalizedHost = NormalizeMatchHost(targetHost);

        foreach (var rule in UpstreamProxyRules)
        {
            if (!RuleMatchesHost(rule.Domain, normalizedHost)) continue;
            return rule.Proxy;
        }

        return null;
    }

    private static string NormalizeMatchHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return string.Empty;

        var span = host.AsSpan().Trim();
        if (span.Length >= 2 && span[0] == '[' && span[^1] == ']')
            span = span[1..^1];

        return span.ToString();
    }

    private static bool RuleMatchesHost(string? domainRule, string host)
    {
        if (string.IsNullOrEmpty(domainRule)) return true;
        if (string.IsNullOrEmpty(host)) return false;

        if (TryParseNetworkRule(domainRule, out var network, out var prefixLength))
        {
            if (!IPAddress.TryParse(host, out var ipAddress)) return false;
            return IsInCidr(ipAddress, network, prefixLength);
        }

        if (domainRule[0] == '.')
            return host.EndsWith(domainRule, StringComparison.OrdinalIgnoreCase);

        return string.Equals(host, domainRule, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseNetworkRule(string value, out IPAddress network, out int prefixLength)
    {
        network = IPAddress.None;
        prefixLength = 0;

        var slashIndex = value.IndexOf('/');
        if (slashIndex <= 0 || slashIndex >= value.Length - 1) return false;

        var networkPart = value.Substring(0, slashIndex);
        var maskPart = value.Substring(slashIndex + 1);

        if (!IPAddress.TryParse(networkPart, out var parsedNetwork) || parsedNetwork is null) return false;
        network = parsedNetwork;

        if (int.TryParse(maskPart, out var parsedPrefix))
        {
            var maxBits = network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            if (parsedPrefix < 0 || parsedPrefix > maxBits) return false;
            prefixLength = parsedPrefix;
            return true;
        }

        if (!IPAddress.TryParse(maskPart, out var maskAddress) || maskAddress is null) return false;
        if (maskAddress.AddressFamily != network.AddressFamily) return false;

        return TryGetPrefixLengthFromMask(maskAddress, out prefixLength);
    }

    private static bool TryGetPrefixLengthFromMask(IPAddress maskAddress, out int prefixLength)
    {
        prefixLength = 0;
        var bytes = maskAddress.GetAddressBytes();
        var seenZero = false;

        foreach (var b in bytes)
            for (var bit = 7; bit >= 0; bit--)
            {
                var isSet = (b & (1 << bit)) != 0;
                if (isSet)
                {
                    if (seenZero) return false;
                    prefixLength++;
                }
                else
                {
                    seenZero = true;
                }
            }

        return true;
    }

    private static bool IsInCidr(IPAddress address, IPAddress network, int prefixLength)
    {
        if (address.AddressFamily != network.AddressFamily) return false;

        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        if (addressBytes.Length != networkBytes.Length) return false;

        var fullBytes = prefixLength / 8;
        var partialBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
            if (addressBytes[i] != networkBytes[i])
                return false;

        if (partialBits == 0 || fullBytes >= addressBytes.Length) return true;

        var mask = (byte)(0xFF << (8 - partialBits));
        return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }

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
    /// <summary>
    /// Gets or sets name.
    /// </summary>
    public required string Name { get; init; }
    /// <summary>
    /// Gets or sets value.
    /// </summary>
    public required string Value { get; init; }
}

/// <summary>
/// Reverse proxy path configuration.
/// </summary>
public sealed record ReversePathConfig
{
    /// <summary>
    /// Gets or sets path.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets or sets url.
    /// </summary>
    public required string Url { get; init; }
}

/// <summary>
/// Upstream proxy configuration.
/// Supports HTTP, SOCKS4, and SOCKS5 proxies.
/// </summary>
public sealed record UpstreamProxyConfig
{
    /// <summary>
    /// Gets or sets host.
    /// </summary>
    public required string Host { get; init; }
    /// <summary>
    /// Gets or sets port.
    /// </summary>
    public required ushort Port { get; init; }
    /// <summary>
    /// Gets or sets username.
    /// </summary>
    public string? Username { get; init; }
    /// <summary>
    /// Gets or sets password.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Gets or sets type.
    /// </summary>
    public UpstreamProxyType Type { get; init; } = UpstreamProxyType.Http;

    /// <summary>
    /// When null, this is the default upstream proxy for all requests.
    /// </summary>
    public string? Domain { get; init; }
}

/// <summary>
/// Ordered upstream routing rule.
/// When Proxy is null, requests matching Domain bypass upstream.
/// </summary>
public sealed record UpstreamProxyRuleConfig
{
    /// <summary>
    /// Gets or sets domain.
    /// </summary>
    public string? Domain { get; init; }

    /// <summary>
    /// Null means "upstream none" (bypass).
    /// </summary>
    public UpstreamProxyConfig? Proxy { get; init; }
}

/// <summary>
/// Ordered ACL rule parsed from Allow/Deny directives.
/// </summary>
public sealed record AclRuleConfig
{
    /// <summary>
    /// True for Allow, false for Deny.
    /// </summary>
    public required bool IsAllow { get; init; }

    /// <summary>
    /// Raw ACL pattern string.
    /// </summary>
    public required string Pattern { get; init; }
}

/// <summary>
/// Upstream proxy type.
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
    /// <summary>
    /// Gets or sets username.
    /// </summary>
    public required string Username { get; init; }
    /// <summary>
    /// Gets or sets password.
    /// </summary>
    public required string Password { get; init; }
    /// <summary>
    /// Gets or sets realm.
    /// </summary>
    public string? Realm { get; init; } = "TinyProxy";
}

/// <summary>
/// Basic authentication user configuration.
/// Supports multiple users.
/// </summary>
public sealed record BasicAuthUser
{
    /// <summary>
    /// Gets or sets username.
    /// </summary>
    public required string Username { get; init; }
    /// <summary>
    /// Gets or sets password.
    /// </summary>
    public required string Password { get; init; }
}
