namespace TinyProxy.Filter;

/// <summary>
/// Filters CONNECT requests by allowed ports.
/// </summary>
public sealed class ConnectFilter
{
    private readonly Configuration _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectFilter"/> class.
    /// </summary>
    public ConnectFilter(Configuration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// If no ports are configured, all ports are allowed.
    /// </summary>
    public bool IsPortAllowed(ushort port)
    {
        if (_config.AllowedConnectPorts.Count == 0) return true;

        return _config.AllowedConnectPorts.Contains(port);
    }

    /// <summary>
    /// Adds allowed port.
    /// </summary>
    public static void AddAllowedPort(ushort port, HashSet<ushort> allowedPorts)
    {
        allowedPorts.Add(port);
    }

    /// <summary>
    /// Empty means all ports are allowed unless ConnectPort directives are configured.
    /// </summary>
    public static HashSet<ushort> DefaultPorts => new();

    /// <summary>
    /// Validates that the port is in a valid range (1-65535).
    /// </summary>
    public static bool IsValidPort(ushort port)
    {
        return port > 0;
    }
}