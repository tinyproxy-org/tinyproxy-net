using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TinyProxy.Config;
using TinyProxy.Core;

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
    private static readonly DateTime ProcessStartTime = DateTime.UtcNow;
    private readonly CancellationTokenSource _cts = new();
    private Task? _serveTask;

    public HealthCheck(Configuration config, ConnectionManager connectionManager, ILogger logger, int healthPort = 9091)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{healthPort}/");
    }

    /// <summary>
    /// Gets the current uptime in seconds.
    /// </summary>
    private static string GetUptime()
    {
        return ((long)(DateTime.UtcNow - ProcessStartTime).TotalSeconds).ToString();
    }

    /// <summary>
    /// Starts health check server asynchronously.
    /// </summary>
    public async Task StartAsync(CancellationToken token = default)
    {
        _listener.Start();
        _serveTask = Task.Run(() => ServeLoopAsync(token), token);
        _logger.LogInfo($"Health check server started on port 9091");
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

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
        }

        _serveTask?.Dispose();
        _cts?.Dispose();
    }
}

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
