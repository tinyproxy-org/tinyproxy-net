namespace TinyProxy.Protocol;

/// <summary>
/// Handles transparent proxy mode.
/// Transparent proxy requires firewall configuration (iptables/pf) to redirect
/// traffic to the proxy without client configuration. The proxy determines the
/// original destination using getsockname() on the client socket.
/// </summary>
public sealed class TransparentProxy
{
    private readonly ILogger _logger;
    private readonly Configuration _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="TransparentProxy"/> class.
    /// </summary>
    public TransparentProxy(ILogger logger, Configuration config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Extracts the target destination from a transparent proxy connection.
    /// </summary>
    /// <param name="clientSocket">The client socket (connection was redirected by firewall)</param>
    /// <param name="request">The parsed HTTP request</param>
    /// <returns>The target host and port, or null if unable to determine</returns>
    public (string host, int port)? GetTransparentDestination(Socket clientSocket, HttpRequest request)
    {
        var hostHeader = GetHostHeader(request.Headers);
        if (!string.IsNullOrEmpty(hostHeader))
            if (TryParseHostPort(hostHeader, out var host, out var port))
            {
                if (IsLocalAddress(host))
                {
                    _logger.LogWarning($"Transparent proxy destination {host} is local, rejecting");
                    return null;
                }

                if (_config.Verbose) _logger.LogInfo($"Transparent proxy using Host header: {host}:{port}");
                return (host, port);
            }

        if (TryGetOriginalDestination(clientSocket, out var destHost, out var destPort))
        {
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
                return Encoding.ASCII.GetString(span);
            }

        return null;
    }

    private static bool TryParseHostPort(string hostHeader, out string host, out int port)
    {
        return TextUtils.TryParseHostPort(hostHeader, 80, out host, out port);
    }

    /// <summary>
    /// This only works when the connection was redirected by firewall rules (iptables REDIRECT, pf rdr, etc.).
    /// </summary>
    private bool TryGetOriginalDestination(Socket clientSocket, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        try
        {
            var endPoint = clientSocket.LocalEndPoint as IPEndPoint;
            if (endPoint == null) return false;

            // In transparent mode, LocalEndPoint carries the original destination.
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
    /// Prevents loops where client tries to connect to the proxy itself.
    /// </summary>
    private bool IsLocalAddress(string host)
    {
        if (_config.ListenAddress != null)
            try
            {
                var listenIp = IPAddress.Parse(_config.ListenAddress);
                var checkIp = IPAddress.Parse(host);

                if (listenIp.Equals(checkIp)) return true;

                if (IPAddress.IsLoopback(checkIp)) return true;
            }
            catch
            {
            }

        return host is "127.0.0.1" or "::1" ||
               string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds an absolute URI for transparent proxy requests.
    /// Transparent proxy requests may be in relative form (without scheme),
    /// so we need to construct the full URL.
    /// </summary>
    public string BuildAbsoluteUri(string requestUri, string host, int port, string? path)
    {
        if (requestUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            requestUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return requestUri;


        var pathStr = path ?? requestUri;
        var hostPart = host;
        if (IPAddress.TryParse(host, out var address) &&
            address.AddressFamily == AddressFamily.InterNetworkV6 &&
            !host.StartsWith('['))
        {
            hostPart = $"[{host}]";
        }

        return $"http://{hostPart}:{port}{pathStr}";
    }
}
