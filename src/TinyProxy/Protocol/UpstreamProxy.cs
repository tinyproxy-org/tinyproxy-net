using System.Net.Sockets;
using System.Text;
using TinyProxy.Config;
using TinyProxy.Core;

namespace TinyProxy.Protocol;

/// <summary>
/// Upstream proxy support for chaining requests.
/// </summary>
public sealed class UpstreamProxy
{
    private readonly Configuration _config;
    private readonly ILogger _logger;

    public UpstreamProxy(Configuration config, ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Checks if upstream proxy is configured.
    /// </summary>
    public bool IsEnabled => _config.UpstreamProxy != null;

    /// <summary>
    /// Creates a socket connected to the upstream proxy.
    /// </summary>
    public async ValueTask<Socket> CreateConnectionAsync(CancellationToken token)
    {
        if (_config.UpstreamProxy == null)
        {
            throw new InvalidOperationException("Upstream proxy not configured");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(
            _config.UpstreamProxy.Host,
            _config.UpstreamProxy.Port,
            _config.Timeout,
            token).ConfigureAwait(false);

        return socket;
    }

    /// <summary>
    /// Adds proxy authentication headers if configured.
    /// </summary>
    public string? GetProxyAuthorizationHeader()
    {
        if (_config.UpstreamProxy?.Username == null ||
            _config.UpstreamProxy?.Password == null)
        {
            return null;
        }

        var credentials = $"{_config.UpstreamProxy.Username}:{_config.UpstreamProxy.Password}";
        var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes(credentials));
        return $"Basic {encoded}";
    }

    /// <summary>
    /// Modifies the request for upstream proxy forwarding.
    /// </summary>
    public byte[] CreateProxyRequest(byte[] originalRequest, string? authHeader = null)
    {
        // For upstream proxy, we send the original request as-is
        // but add Proxy-Authorization header if configured
        if (authHeader == null)
        {
            return originalRequest;
        }

        // Parse and add the Proxy-Authorization header
        var original = Encoding.ASCII.GetString(originalRequest);
        var headerEnd = original.IndexOf("\r\n\r\n", StringComparison.Ordinal);

        if (headerEnd < 0)
        {
            return originalRequest;
        }

        var headers = original.Substring(0, headerEnd);
        var body = original.Substring(headerEnd + 4);

        var modified = headers + $"\r\nProxy-Authorization: {authHeader}\r\n\r\n" + body;
        return Encoding.ASCII.GetBytes(modified);
    }
}
