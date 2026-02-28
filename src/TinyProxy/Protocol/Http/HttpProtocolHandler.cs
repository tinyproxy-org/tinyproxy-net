namespace TinyProxy.Protocol.Http;

/// <summary>
/// Protocol handler for standard HTTP requests.
/// Handles GET, POST, PUT, DELETE, etc.
/// Aligns with tinyproxy C's http_handler functionality.
/// </summary>
public sealed class HttpProtocolHandler : IProtocolHandler
{
    private readonly ILogger _logger;
    private readonly Configuration _config;
    private readonly Stats _stats;
    private readonly AccessLogger _accessLogger;
    private readonly string _clientIp;

    public string ProtocolName => "HTTP";

    public HttpProtocolHandler(
        ILogger logger,
        Configuration config,
        Stats stats,
        AccessLogger accessLogger,
        string clientIp)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _accessLogger = accessLogger ?? throw new ArgumentNullException(nameof(accessLogger));
        _clientIp = clientIp ?? "unknown";
    }

    public async ValueTask<ProcessingResult> ProcessAsync(
        Connection connection,
        HttpRequest request,
        CancellationToken token)
    {
        var forwarder = new HttpForwarder(_logger, _config, _stats, _accessLogger, _clientIp);

        // Standard HTTP forwarding (handles upstream proxy internally)
        await forwarder.ForwardAsync(connection, request, token);
        return new ProcessingResult { Success = true, StatusCode = 200, BytesTransferred = 0 };
    }
}