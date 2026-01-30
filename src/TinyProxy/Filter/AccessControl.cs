using System.Net;
using TinyProxy.Config;

namespace TinyProxy.Filter;

/// <summary>
/// IP-based access control (whitelist/blacklist).
/// </summary>
public sealed class AccessControl
{
    private readonly Configuration _config;

    public AccessControl(Configuration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Checks if a client IP is allowed to connect.
    /// </summary>
    public bool IsAllowed(string clientIp)
    {
        // If whitelist has entries, only allow IPs in whitelist
        if (_config.AllowIPs.Count > 0)
        {
            return IsMatch(clientIp, _config.AllowIPs);
        }

        // If denylist has entries, deny IPs in denylist
        if (_config.DenyIPs.Count > 0)
        {
            return !IsMatch(clientIp, _config.DenyIPs);
        }

        // No filtering configured, allow all
        return true;
    }

    /// <summary>
    /// Checks if a client IP is allowed to connect.
    /// </summary>
    public bool IsAllowed(EndPoint endPoint)
    {
        if (endPoint is IPEndPoint ipEndPoint)
        {
            return IsAllowed(ipEndPoint.Address.ToString());
        }

        // Allow non-IP endpoints (e.g., Unix domain sockets)
        return true;
    }

    private static bool IsMatch(string ip, IEnumerable<string> patterns)
    {
        // Try exact match first
        if (patterns.Contains(ip))
        {
            return true;
        }

        // Try CIDR notation and wildcards
        foreach (var pattern in patterns)
        {
            if (MatchesPattern(ip, pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesPattern(string ip, string pattern)
    {
        // Simple wildcard support (e.g., 192.168.*.*)
        if (pattern.Contains('*'))
        {
            var patternParts = pattern.Split('.');
            var ipParts = ip.Split('.');

            if (patternParts.Length != ipParts.Length)
            {
                return false;
            }

            for (int i = 0; i < patternParts.Length; i++)
            {
                if (patternParts[i] != "*" && patternParts[i] != ipParts[i])
                {
                    return false;
                }
            }

            return true;
        }

        // CIDR notation could be added here for more advanced matching

        return false;
    }
}
