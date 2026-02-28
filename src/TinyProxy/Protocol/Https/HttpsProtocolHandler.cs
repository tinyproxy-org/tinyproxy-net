namespace TinyProxy.Protocol.Https;

/// <summary>
/// Protocol handler for HTTPS/CONNECT tunneling.
/// Handles CONNECT method for SSL/TLS tunneling.
/// Aligns with tinyproxy C's connect_method handling.
/// </summary>
public sealed class HttpsProtocolHandler : IProtocolHandler
{
    private readonly ILogger _logger;
    private readonly Configuration _config;
    private readonly Stats _stats;
    private readonly AccessLogger _accessLogger;
    private readonly string _clientIp;

    public string ProtocolName => "HTTPS/CONNECT";

    public HttpsProtocolHandler(
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
        var connectHandler = new ConnectHandler(_logger, _config, _stats, _accessLogger, _clientIp);

        // Handle CONNECT tunneling
        await connectHandler.HandleConnectAsync(connection, request, request.Body, token);

        return new ProcessingResult { Success = true, StatusCode = 200, BytesTransferred = 0 };
    }
}