using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinyProxy.Metrics;

/// <summary>
/// Health check endpoint for monitoring.
/// Returns JSON health status.
/// </summary>
public sealed class HealthCheck : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Configuration _config;
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger _logger;
    private readonly int _healthPort;
    private static readonly DateTime ProcessStartTime = DateTime.UtcNow;
    private Task? _serveTask;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="HealthCheck"/> class.
    /// </summary>
    public HealthCheck(Configuration config, ConnectionManager connectionManager, ILogger logger, int healthPort = 9091)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (healthPort <= 0 || healthPort > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(healthPort), healthPort, "Port must be between 1 and 65535.");
        _healthPort = healthPort;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{healthPort}/");
    }

    private static string GetUptime()
    {
        return ((long)(DateTime.UtcNow - ProcessStartTime).TotalSeconds).ToString();
    }

    /// <summary>
    /// Starts health check server asynchronously.
    /// </summary>
    public Task StartAsync(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        _listener.Start();
        _serveTask = Task.Run(() => ServeLoopAsync(token));
        _logger.LogInfo($"Health check server started on port {_healthPort}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Main serve loop for handling health requests.
    /// </summary>
    private async Task ServeLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var ctx = await _listener.GetContextAsync().ConfigureAwait(false);

                if (ctx.Request.Url?.AbsolutePath == "/health")
                {
                    await HandleHealthRequestAsync(ctx).ConfigureAwait(false);
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }
            }
        }
        catch (HttpListenerException)
        {
            // Expected when stopping
        }
        catch (Exception ex)
        {
            _logger.LogError($"Health check server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles a health check request.
    /// </summary>
    private async Task HandleHealthRequestAsync(HttpListenerContext ctx)
    {
        var status = _connectionManager.ActiveCount < _config.MaxClients ? "healthy" : "overloaded";
        var uptime = GetUptime();

        var response = new HealthResponse(
            status,
            _connectionManager.ActiveCount.ToString(),
            _config.MaxClients.ToString(),
            uptime);

        var json = JsonSerializer.Serialize(response, HealthJsonSerializerContext.Default.HealthResponse);
        var buffer = Encoding.UTF8.GetBytes(json);

        ctx.Response.ContentType = "application/health+json";
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentLength64 = buffer.Length;

        await ctx.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
        ctx.Response.Close();
    }

    /// <summary>
    /// Releases the resources used by this instance.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
        }
    }
}

/// <summary>
/// Represents the health endpoint payload.
/// </summary>
/// <param name="status">Current service health status.</param>
/// <param name="activeConnections">Current active connection count.</param>
/// <param name="maxConnections">Configured maximum connection count.</param>
/// <param name="uptimeSeconds">Process uptime in seconds.</param>
public sealed record HealthResponse(
    string status,
    string activeConnections,
    string maxConnections,
    string uptimeSeconds);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HealthResponse))]
internal sealed partial class HealthJsonSerializerContext : JsonSerializerContext
{
}
