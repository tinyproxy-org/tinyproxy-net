using System.Collections.Concurrent;

namespace TinyProxy.Filter;

/// <summary>
/// IP-based access control (whitelist/blacklist).
/// Aligns with tinyproxy C's acl.c implementation.
/// </summary>
public sealed class AccessControl
{
    private readonly Configuration _config;
    private readonly List<AccessRule> _allowRules;
    private readonly List<AccessRule> _denyRules;
    private readonly ConcurrentDictionary<string, string> _dnsCache;
    private readonly ConcurrentDictionary<string, IPAddress> _dnsForwardCache;

    public AccessControl(Configuration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _allowRules = ParseRules(config.AllowIPs, AccessType.Allow);
        _denyRules = ParseRules(config.DenyIPs, AccessType.Deny);
        _dnsCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _dnsForwardCache = new ConcurrentDictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if a client IP is allowed to connect.
    /// Aligns with tinyproxy C's check_acl function.
    /// </summary>
    public bool IsAllowed(string clientIp)
    {
        // If allow rules have entries, only allow IPs that match allow rules
        if (_allowRules.Count > 0) return CheckRules(clientIp, _allowRules, _dnsForwardCache);

        // If deny rules have entries, deny IPs that match deny rules
        if (_denyRules.Count > 0) return !CheckRules(clientIp, _denyRules, _dnsForwardCache);

        // No filtering configured, allow all
        return true;
    }

    /// <summary>
    /// Checks if a client IP is allowed to connect (EndPoint overload).
    /// </summary>
    public bool IsAllowed(EndPoint endPoint)
    {
        if (endPoint is IPEndPoint ipEndPoint) return IsAllowed(ipEndPoint.Address.ToString());

        // Allow non-IP endpoints (e.g., Unix domain sockets)
        return true;
    }

    /// <summary>
    /// Checks if a connection from a specific hostname is allowed.
    /// Performs DNS lookup to resolve hostname to IP(s) and checks ACL.
    /// Aligns with tinyproxy C's acl_string_processing.
    /// </summary>
    public async Task<bool> IsAllowedAsync(string hostname, CancellationToken cancellationToken = default)
    {
        // First, check direct IP pattern match
        if (IPAddress.TryParse(hostname, out var ipAddress)) return IsAllowed(ipAddress.ToString());

        // Try to resolve hostname to IP and check
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(hostname, cancellationToken).ConfigureAwait(false);
            foreach (var address in addresses)
                if (!IsAllowed(address.ToString()))
                    return false;

            return true;
        }
        catch (HttpRequestException)
        {
            // DNS resolution failed, deny
            return false;
        }
        catch (SocketException)
        {
            // DNS resolution failed, deny
            return false;
        }
    }

    /// <summary>
    /// Checks if a connection from a socket is allowed.
    /// Performs reverse DNS lookup if needed.
    /// Aligns with tinyproxy C's acl_string_processing with getnameinfo.
    /// </summary>
    public async Task<bool> IsAllowedAsync(Socket socket, CancellationToken cancellationToken = default)
    {
        if (socket.RemoteEndPoint is IPEndPoint ipEndPoint) return await IsAllowedAsync(ipEndPoint.Address, ipEndPoint, cancellationToken);

        return true;
    }

    /// <summary>
    /// Checks if an IP address with its associated EndPoint is allowed.
    /// Performs reverse DNS lookup for string-based rules.
    /// </summary>
    private async Task<bool> IsAllowedAsync(IPAddress ipAddress, IPEndPoint endPoint, CancellationToken cancellationToken)
    {
        var ipString = ipAddress.ToString();

        // Check numeric rules first (fast path)
        if (_allowRules.Count > 0)
        {
            if (HasNumericMatch(ipString, _allowRules)) return true;
            // Check if allow rules require DNS lookup
            if (_allowRules.Any(r => r.Type == RuleType.Domain))
            {
                var hostname = await GetHostnameAsync(endPoint, cancellationToken);
                return CheckDomainMatch(hostname, _allowRules);
            }

            return false;
        }

        if (_denyRules.Count > 0)
        {
            if (HasNumericMatch(ipString, _denyRules)) return false;
            // Check if deny rules require DNS lookup
            if (_denyRules.Any(r => r.Type == RuleType.Domain))
            {
                var hostname = await GetHostnameAsync(endPoint, cancellationToken);
                return !CheckDomainMatch(hostname, _denyRules);
            }
        }

        return true;
    }

    /// <summary>
    /// Parses configuration rules into AccessRule objects.
    /// </summary>
    private static List<AccessRule> ParseRules(IEnumerable<string> patterns, AccessType accessType)
    {
        var rules = new List<AccessRule>();
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;

            var rule = ParseRule(pattern, accessType);
            if (rule != null) rules.Add(rule);
        }

        return rules;
    }

    /// <summary>
    /// Parses a single rule pattern.
    /// Supports: IP, CIDR, wildcard (*), and domain suffix (.domain.com).
    /// </summary>
    private static AccessRule? ParseRule(string pattern, AccessType accessType)
    {
        pattern = pattern.Trim();

        // Check for domain suffix (starts with '.')
        if (pattern.StartsWith('.')) return new AccessRule(RuleType.Domain, pattern.Substring(1), accessType);

        // Check for wildcard pattern
        if (pattern.Contains('*')) return new AccessRule(RuleType.Wildcard, pattern, accessType);

        // Check for CIDR notation
        var slashIndex = pattern.IndexOf('/');
        if (slashIndex > 0 && slashIndex < pattern.Length - 1)
        {
            var ipPart = pattern.Substring(0, slashIndex);
            var prefixLength = pattern.Substring(slashIndex + 1);
            if (IPAddress.TryParse(ipPart, out var ipAddress) && int.TryParse(prefixLength, out var prefixLen))
                return new AccessRule(RuleType.Cidr, pattern, accessType, ipAddress, prefixLen);
        }

        // Try as plain IP address
        if (IPAddress.TryParse(pattern, out var ip)) return new AccessRule(RuleType.Ip, pattern, accessType, ip);

        // Treat as domain name
        return new AccessRule(RuleType.Domain, pattern, accessType);
    }

    /// <summary>
    /// Checks if an IP matches any of the given rules.
    /// Fast path for numeric IP/CIDR/wildcard rules.
    /// </summary>
    private static bool CheckRules(string ip, List<AccessRule> rules, ConcurrentDictionary<string, IPAddress> dnsCache)
    {
        foreach (var rule in rules)
            switch (rule.Type)
            {
                case RuleType.Ip:
                    if (string.Equals(ip, rule.Pattern, StringComparison.OrdinalIgnoreCase))
                        return true;
                    break;

                case RuleType.Cidr:
                    if (rule.IPAddress != null && IPAddress.TryParse(ip, out var ipAddr))
                        if (IsInSubnet(ipAddr, rule.IPAddress, rule.PrefixLength))
                            return true;
                    break;

                case RuleType.Wildcard:
                    if (MatchWildcard(ip, rule.Pattern))
                        return true;
                    break;

                case RuleType.Domain:
                    // Domain rules require DNS lookup, handled separately
                    break;
            }

        return false;
    }

    /// <summary>
    /// Checks if an IP matches any numeric rules (IP, CIDR, wildcard).
    /// </summary>
    private static bool HasNumericMatch(string ip, List<AccessRule> rules)
    {
        foreach (var rule in rules)
            switch (rule.Type)
            {
                case RuleType.Ip:
                    if (string.Equals(ip, rule.Pattern, StringComparison.OrdinalIgnoreCase))
                        return true;
                    break;

                case RuleType.Cidr:
                    if (rule.IPAddress != null && IPAddress.TryParse(ip, out var ipAddr))
                        if (IsInSubnet(ipAddr, rule.IPAddress, rule.PrefixLength))
                            return true;
                    break;

                case RuleType.Wildcard:
                    if (MatchWildcard(ip, rule.Pattern))
                        return true;
                    break;
            }

        return false;
    }

    /// <summary>
    /// Checks if a hostname matches any domain rules.
    /// </summary>
    private static bool CheckDomainMatch(string hostname, List<AccessRule> rules)
    {
        foreach (var rule in rules)
            if (rule.Type == RuleType.Domain)
            {
                // Check suffix match
                if (hostname.EndsWith(rule.Pattern, StringComparison.OrdinalIgnoreCase))
                    // Ensure it's a whole domain boundary (either matches exactly or has a dot before)
                    if (hostname.Length == rule.Pattern.Length ||
                        hostname[hostname.Length - rule.Pattern.Length - 1] == '.')
                        return true;

                // Check exact match
                if (string.Equals(hostname, rule.Pattern, StringComparison.OrdinalIgnoreCase)) return true;
            }

        return false;
    }

    /// <summary>
    /// Performs reverse DNS lookup with caching.
    /// Aligns with tinyproxy C's getnameinfo call in acl_string_processing.
    /// </summary>
    private async Task<string> GetHostnameAsync(IPEndPoint endPoint, CancellationToken cancellationToken)
    {
        var cacheKey = endPoint.Address.ToString();

        if (_dnsCache.TryGetValue(cacheKey, out var cachedHostname)) return cachedHostname;

        try
        {
            var hostEntry = await Dns.GetHostEntryAsync(endPoint.Address.ToString(), cancellationToken).ConfigureAwait(false);
            var hostname = hostEntry.HostName;

            // Cache with LRU eviction
            if (_dnsCache.Count >= ProxyConstants.MaxDnsCacheSize)
            {
                // Remove some entries to keep cache size under control
                var keysToRemove = _dnsCache.Keys.Take(100).ToList();
                foreach (var key in keysToRemove) _dnsCache.TryRemove(key, out _);
            }

            _dnsCache.TryAdd(cacheKey, hostname);
            return hostname;
        }
        catch (SocketException)
        {
            // Reverse DNS failed
            return string.Empty;
        }
    }

    /// <summary>
    /// Checks if an IP address is within a CIDR subnet.
    /// </summary>
    private static bool IsInSubnet(IPAddress ipAddress, IPAddress subnet, int prefixLength)
    {
        if (ipAddress.AddressFamily != subnet.AddressFamily)
            return false;

        var ipBytes = ipAddress.GetAddressBytes();
        var subnetBytes = subnet.GetAddressBytes();

        if (ipBytes.Length != subnetBytes.Length)
            return false;

        var fullBytes = prefixLength / 8;
        var partialBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
            if (ipBytes[i] != subnetBytes[i])
                return false;

        if (partialBits > 0 && fullBytes < ipBytes.Length)
        {
            var mask = (byte)(0xFF << (8 - partialBits));
            if ((ipBytes[fullBytes] & mask) != (subnetBytes[fullBytes] & mask))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Matches an IP against a wildcard pattern (e.g., 192.168.*.*).
    /// </summary>
    private static bool MatchWildcard(string ip, string pattern)
    {
        var patternParts = pattern.Split('.');
        var ipParts = ip.Split('.');

        if (patternParts.Length != ipParts.Length)
            return false;

        for (var i = 0; i < patternParts.Length; i++)
            if (patternParts[i] != "*" && patternParts[i] != ipParts[i])
                return false;

        return true;
    }
}

/// <summary>
/// Represents a single access control rule.
/// </summary>
internal sealed class AccessRule
{
    public RuleType Type { get; }
    public string Pattern { get; }
    public AccessType AccessType { get; }
    public IPAddress? IPAddress { get; }
    public int PrefixLength { get; }

    public AccessRule(RuleType type, string pattern, AccessType accessType, IPAddress? ipAddress = null, int prefixLength = 0)
    {
        Type = type;
        Pattern = pattern;
        AccessType = accessType;
        IPAddress = ipAddress;
        PrefixLength = prefixLength;
    }
}

/// <summary>
/// Type of rule (IP, CIDR, wildcard, domain).
/// </summary>
internal enum RuleType
{
    Ip,
    Cidr,
    Wildcard,
    Domain
}

/// <summary>
/// Access control type (allow or deny).
/// </summary>
internal enum AccessType
{
    Allow,
    Deny
}