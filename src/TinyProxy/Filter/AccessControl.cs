using System.Collections.Concurrent;

namespace TinyProxy.Filter;

/// <summary>
/// IP/domain access control.
/// Aligns with tinyproxy C's acl.c implementation.
/// </summary>
public sealed class AccessControl
{
    private readonly List<AccessRule> _orderedRules;
    private readonly bool _aclConfigured;
    private readonly ConcurrentDictionary<string, string> _dnsCache;
    private readonly ConcurrentDictionary<string, IPAddress[]> _dnsForwardCache;

    public AccessControl(Configuration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        _orderedRules = BuildOrderedRules(config);
        _aclConfigured = HasConfiguredAclDirectives(config);
        _dnsCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _dnsForwardCache = new ConcurrentDictionary<string, IPAddress[]>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks whether a client IP/hostname is allowed.
    /// </summary>
    public bool IsAllowed(string clientIp)
    {
        if (_orderedRules.Count == 0) return !_aclConfigured;

        var isIpAddress = IPAddress.TryParse(clientIp, out var parsedIpAddress);
        var normalizedParsedIp = isIpAddress && parsedIpAddress != null
            ? NormalizeIpAddress(parsedIpAddress)
            : null;
        var candidate = normalizedParsedIp?.ToString() ?? clientIp;

        foreach (var rule in _orderedRules)
        {
            if (!TryMatchRuleSync(rule, candidate, normalizedParsedIp, isIpAddress)) continue;
            return rule.AccessType == AccessType.Allow;
        }

        // tinyproxy C default when ACL exists: deny.
        return false;
    }

    /// <summary>
    /// Checks whether a client endpoint is allowed.
    /// </summary>
    public bool IsAllowed(EndPoint endPoint)
    {
        if (endPoint is IPEndPoint ipEndPoint) return IsAllowed(ipEndPoint.Address.ToString());

        return _orderedRules.Count == 0 && !_aclConfigured;
    }

    /// <summary>
    /// Checks whether a client hostname is allowed.
    /// </summary>
    public async Task<bool> IsAllowedAsync(string hostname, CancellationToken cancellationToken = default)
    {
        if (_orderedRules.Count == 0) return !_aclConfigured;

        if (IPAddress.TryParse(hostname, out var ipAddress))
            return IsAllowed(ipAddress.ToString());

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(hostname, cancellationToken).ConfigureAwait(false);
            foreach (var address in addresses)
            {
                var endpoint = new IPEndPoint(address, 0);
                if (!await IsAllowedAsync(address, endpoint, cancellationToken).ConfigureAwait(false))
                    return false;
            }

            return addresses.Length > 0;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether a client socket is allowed.
    /// </summary>
    public async Task<bool> IsAllowedAsync(Socket socket, CancellationToken cancellationToken = default)
    {
        if (socket.RemoteEndPoint is IPEndPoint ipEndPoint)
            return await IsAllowedAsync(ipEndPoint.Address, ipEndPoint, cancellationToken).ConfigureAwait(false);

        return _orderedRules.Count == 0 && !_aclConfigured;
    }

    private async Task<bool> IsAllowedAsync(IPAddress ipAddress, IPEndPoint endPoint, CancellationToken cancellationToken)
    {
        if (_orderedRules.Count == 0) return !_aclConfigured;

        ipAddress = NormalizeIpAddress(ipAddress);
        var ipString = ipAddress.ToString();
        string? hostname = null;

        foreach (var rule in _orderedRules)
        {
            var matched = false;

            switch (rule.Type)
            {
                case RuleType.Ip:
                    matched = string.Equals(ipString, rule.Pattern, StringComparison.OrdinalIgnoreCase);
                    break;

                case RuleType.Cidr:
                    matched = rule.IPAddress != null && IsInSubnet(ipAddress, rule.IPAddress, rule.PrefixLength);
                    break;

                case RuleType.Wildcard:
                    matched = MatchWildcard(ipString, rule.Pattern);
                    break;

                case RuleType.Domain:
                    if (!rule.Pattern.StartsWith(".", StringComparison.Ordinal) &&
                        await ForwardLookupContainsIpAsync(rule.Pattern, ipAddress, cancellationToken).ConfigureAwait(false))
                    {
                        matched = true;
                        break;
                    }

                    hostname ??= await GetHostnameAsync(endPoint, cancellationToken).ConfigureAwait(false);
                    matched = DomainPatternMatches(hostname, rule.Pattern);
                    break;
            }

            if (matched)
                return rule.AccessType == AccessType.Allow;
        }

        return false;
    }

    private static List<AccessRule> BuildOrderedRules(Configuration config)
    {
        var rules = new List<AccessRule>();

        if (config.AccessRules.Count > 0)
        {
            foreach (var configuredRule in config.AccessRules)
            {
                if (string.IsNullOrWhiteSpace(configuredRule.Pattern)) continue;
                var parsedRule = ParseRule(
                    configuredRule.Pattern,
                    configuredRule.IsAllow ? AccessType.Allow : AccessType.Deny);
                if (parsedRule != null) rules.Add(parsedRule);
            }

            return rules;
        }

        // Backward compatibility for in-memory configs built via AllowIPs/DenyIPs.
        // Ordering is only guaranteed when AccessRules is populated by parser.
        rules.AddRange(ParseRules(config.AllowIPs, AccessType.Allow));
        rules.AddRange(ParseRules(config.DenyIPs, AccessType.Deny));
        return rules;
    }

    private static bool HasConfiguredAclDirectives(Configuration config)
    {
        return config.AccessRules.Count > 0 || config.AllowIPs.Count > 0 || config.DenyIPs.Count > 0;
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
    /// Supports: IP, CIDR, wildcard (*), and domain pattern.
    /// </summary>
    private static AccessRule? ParseRule(string pattern, AccessType accessType)
    {
        pattern = pattern.Trim();

        // Keep the leading dot to preserve tinyproxy C semantics:
        // ".example.com" must not match bare "example.com".
        if (pattern.StartsWith(".", StringComparison.Ordinal))
            return new AccessRule(RuleType.Domain, pattern, accessType);

        if (pattern.Contains('*'))
            return new AccessRule(RuleType.Wildcard, pattern, accessType);

        var slashIndex = pattern.IndexOf('/');
        if (slashIndex > 0 && slashIndex < pattern.Length - 1)
        {
            var ipPart = pattern.Substring(0, slashIndex);
            var prefixLength = pattern.Substring(slashIndex + 1);
            if (IPAddress.TryParse(ipPart, out var ipAddress) && int.TryParse(prefixLength, out var prefixLen))
            {
                var normalized = NormalizeIpAddress(ipAddress);
                var maxPrefixLength = normalized.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
                if (prefixLen < 0 || prefixLen > maxPrefixLength)
                    return null;

                return new AccessRule(RuleType.Cidr, pattern, accessType, normalized, prefixLen);
            }

            return null;
        }

        if (pattern.Contains('/'))
            return null;

        if (IPAddress.TryParse(pattern, out var ip))
            return new AccessRule(RuleType.Ip, NormalizeIpAddress(ip).ToString(), accessType, NormalizeIpAddress(ip));

        return new AccessRule(RuleType.Domain, pattern, accessType);
    }

    private bool TryMatchRuleSync(AccessRule rule, string candidate, IPAddress? parsedIpAddress, bool isIpAddress)
    {
        switch (rule.Type)
        {
            case RuleType.Ip:
                return isIpAddress && string.Equals(candidate, rule.Pattern, StringComparison.OrdinalIgnoreCase);

            case RuleType.Cidr:
                return isIpAddress &&
                       parsedIpAddress != null &&
                       rule.IPAddress != null &&
                       IsInSubnet(parsedIpAddress, rule.IPAddress, rule.PrefixLength);

            case RuleType.Wildcard:
                return isIpAddress && MatchWildcard(candidate, rule.Pattern);

            case RuleType.Domain:
                if (isIpAddress)
                {
                    if (parsedIpAddress == null || rule.Pattern.StartsWith(".", StringComparison.Ordinal))
                        return false;

                    return ForwardLookupContainsIp(rule.Pattern, parsedIpAddress);
                }

                return DomainPatternMatches(candidate, rule.Pattern);

            default:
                return false;
        }
    }

    private static bool DomainPatternMatches(string hostname, string pattern)
    {
        if (string.IsNullOrEmpty(hostname)) return false;
        if (hostname.Length < pattern.Length) return false;
        return hostname.EndsWith(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private bool ForwardLookupContainsIp(string host, IPAddress ipAddress)
    {
        if (!_dnsForwardCache.TryGetValue(host, out var addresses))
        {
            try
            {
                addresses = Dns.GetHostAddresses(host);
            }
            catch (SocketException)
            {
                addresses = Array.Empty<IPAddress>();
            }

            CacheForwardLookup(host, addresses);
        }

        foreach (var address in addresses)
        {
            if (AreEquivalentIpAddresses(address, ipAddress)) return true;
        }

        return false;
    }

    private async ValueTask<bool> ForwardLookupContainsIpAsync(string host, IPAddress ipAddress, CancellationToken cancellationToken)
    {
        if (!_dnsForwardCache.TryGetValue(host, out var addresses))
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                addresses = Array.Empty<IPAddress>();
            }

            CacheForwardLookup(host, addresses);
        }

        foreach (var address in addresses)
        {
            if (AreEquivalentIpAddresses(address, ipAddress)) return true;
        }

        return false;
    }

    private void CacheForwardLookup(string host, IPAddress[] addresses)
    {
        if (_dnsForwardCache.Count >= ProxyConstants.MaxDnsCacheSize)
        {
            var keysToRemove = _dnsForwardCache.Keys.Take(100).ToList();
            foreach (var key in keysToRemove)
                _dnsForwardCache.TryRemove(key, out _);
        }

        _dnsForwardCache[host] = addresses;
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
                var keysToRemove = _dnsCache.Keys.Take(100).ToList();
                foreach (var key in keysToRemove) _dnsCache.TryRemove(key, out _);
            }

            _dnsCache.TryAdd(cacheKey, hostname);
            return hostname;
        }
        catch (SocketException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Checks if an IP address is within a CIDR subnet.
    /// </summary>
    private static bool IsInSubnet(IPAddress ipAddress, IPAddress subnet, int prefixLength)
    {
        ipAddress = NormalizeIpAddress(ipAddress);
        subnet = NormalizeIpAddress(subnet);

        if (ipAddress.AddressFamily != subnet.AddressFamily)
            return false;

        var ipBytes = ipAddress.GetAddressBytes();
        var subnetBytes = subnet.GetAddressBytes();

        if (ipBytes.Length != subnetBytes.Length)
            return false;

        var maxPrefixLength = ipBytes.Length * 8;
        if (prefixLength < 0 || prefixLength > maxPrefixLength)
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

    private static IPAddress NormalizeIpAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();

        return address;
    }

    private static bool AreEquivalentIpAddresses(IPAddress left, IPAddress right)
    {
        return NormalizeIpAddress(left).Equals(NormalizeIpAddress(right));
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
