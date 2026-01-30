using TinyProxy.Config;

namespace TinyProxy.Filter;

/// <summary>
/// Filters CONNECT requests by allowed ports.
/// Aligns with tinyproxy C's connect-ports.c implementation.
/// </summary>
public sealed class ConnectFilter
{
    private readonly Configuration _config;

    public ConnectFilter(Configuration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Checks if the port is allowed for CONNECT requests.
    /// Aligns with tinyproxy C's check_allowed_connect_ports function.
    /// If no ports are configured, all ports are allowed.
    /// </summary>
    public bool IsPortAllowed(ushort port)
    {
        // If no ports configured, allow all ports
        if (_config.AllowedConnectPorts.Count == 0)
        {
            return true;
        }

        return _config.AllowedConnectPorts.Contains(port);
    }

    /// <summary>
    /// Adds a port to the allowed list.
    /// Aligns with tinyproxy C's add_connect_port_allowed function.
    /// </summary>
    public static void AddAllowedPort(ushort port, HashSet<ushort> allowedPorts)
    {
        allowedPorts.Add(port);
    }

    /// <summary>
    /// Gets the default allowed ports for CONNECT.
    /// Default is only port 443 (HTTPS).
    /// </summary>
    public static HashSet<ushort> DefaultPorts => new() { 443 };

    /// <summary>
    /// Validates that the port is in a valid range (1-65535).
    /// </summary>
    public static bool IsValidPort(ushort port) => port > 0;
}
