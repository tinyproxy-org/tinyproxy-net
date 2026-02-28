namespace TinyProxy.Protocol;

/// <summary>
/// Handles transparent proxy mode.
/// Aligns with tinyproxy C's transparent-proxy.c
///
/// Transparent proxy requires firewall configuration (iptables/pf) to redirect
/// traffic to the proxy without client configuration. The proxy determines the
/// original destination using getsockname() on the client socket.
/// </summary>
public sealed class TransparentProxy
{
    private readonly ILogger _logger;
    private readonly Configuration _config;

    public TransparentProxy(ILogger logger, Configuration config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Extracts the target destination from a transparent proxy connection.
    /// Aligns with tinyproxy C's do_transparent_proxy() function.
    /// </summary>
    /// <param name="clientSocket">The client socket (connection was redirected by firewall)</param>
    /// <param name="request">The parsed HTTP request</param>
    /// <returns>The target host and port, or null if unable to determine</returns>
    public (string host, int port)? GetTransparentDestination(Socket clientSocket, HttpRequest request)
    {
        // First, check if the request has a Host header
        var hostHeader = GetHostHeader(request.Headers);
        if (!string.IsNullOrEmpty(hostHeader))
            if (TryParseHostPort(hostHeader, out var host, out var port))
            {
                if (_config.Verbose) _logger.LogInfo($"Transparent proxy using Host header: {host}:{port}");
                return (host, port);
            }

        // No Host header, use getsockname() to get the original destination
        // This is the key to transparent proxy - the firewall redirected the connection
        // to us, but the socket still knows the original destination
        if (TryGetOriginalDestination(clientSocket, out var destHost, out var destPort))
        {
            // Prevent connections to the proxy itself
            if (IsLocalAddress(destHost))
            {
                _logger.LogWarning($"Transparent proxy destination {destHost} is local, rejecting");
                return null;
            }

            if (_config.Verbose) _logger.LogInfo($"Transparent proxy using getsockname: {destHost}:{destPort}");

            return (destHost, destPort);
        }

        _logger.LogWarning("Transparent proxy unable to determine destination");
        return null;
    }

    /// <summary>
    /// Extracts the Host header from the request headers.
    /// </summary>
    private static string? GetHostHeader(IDictionary<string, ReadOnlySequence<byte>> headers)
    {
        foreach (var kvp in headers)
            if (string.Equals(kvp.Key, "Host", StringComparison.OrdinalIgnoreCase))
            {
                var span = kvp.Value.IsSingleSegment ? kvp.Value.FirstSpan : kvp.Value.ToArray();
                return System.Text.Encoding.ASCII.GetString(span);
            }

        return null;
    }

    /// <summary>
    /// Parses a host:port string using shared utility.
    /// </summary>
    private static bool TryParseHostPort(string hostHeader, out string host, out int port)
    {
        return TextUtils.TryParseHostPort(hostHeader, 80, out host, out port);
    }

    /// <summary>
    /// Gets the original destination address using getsockname().
    /// This only works when the connection was redirected by firewall rules (iptables REDIRECT, pf rdr, etc.).
    /// Aligns with tinyproxy C's use of getsockname() in do_transparent_proxy().
    /// </summary>
    private bool TryGetOriginalDestination(Socket clientSocket, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        try
        {
            var endPoint = clientSocket.LocalEndPoint as IPEndPoint;
            if (endPoint == null) return false;

            // In transparent proxy mode, LocalEndPoint gives us the ORIGINAL destination
            // (not the proxy's listening address) because of the firewall redirect
            host = endPoint.Address.ToString();
            port = endPoint.Port;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"GetOriginalDestination error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Checks if the given address is a local address (proxy's listen address).
    /// Prevents loops where client tries to connect to the proxy itself.
    /// Aligns with tinyproxy C's check against listen_addrs.
    /// </summary>
    private bool IsLocalAddress(string host)
    {
        // Check against configured listen addresses
        if (_config.ListenAddress != null)
            try
            {
                var listenIp = IPAddress.Parse(_config.ListenAddress);
                var checkIp = IPAddress.Parse(host);

                if (listenIp.Equals(checkIp)) return true;

                // Check for loopback
                if (IPAddress.IsLoopback(checkIp)) return true;
            }
            catch
            {
                // Parse failed, ignore
            }

        // Also check common local addresses
        return host is "127.0.0.1" or "::1" or "localhost";
    }

    /// <summary>
    /// Builds an absolute URI for transparent proxy requests.
    /// Transparent proxy requests may be in relative form (without scheme),
    /// so we need to construct the full URL.
    /// </summary>
    public string BuildAbsoluteUri(string requestUri, string host, int port, string? path)
    {
        // If the request URI is already absolute, use it as-is
        if (requestUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            requestUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return requestUri;

        // Build absolute URI from components
        // Omit port for standard HTTP port (80) - matches tinyproxy C behavior
        var portSuffix = port == 80 ? "" : $":{port}";
        var pathStr = path ?? requestUri;

        return $"http://{host}{portSuffix}{pathStr}";
    }
}
