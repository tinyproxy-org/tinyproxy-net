using TinyProxy.Config;

namespace TinyProxy.Filter;

/// <summary>
/// Filters CONNECT requests by allowed ports.
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
    /// </summary>
    public bool IsPortAllowed(ushort port)
    {
        return _config.AllowedConnectPorts.Contains(port);
    }

    /// <summary>
    /// Gets the default allowed ports for CONNECT.
    /// </summary>
    public static HashSet<ushort> DefaultPorts => new() { 443 };
}
