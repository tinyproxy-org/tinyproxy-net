namespace TinyProxy.Metrics;

/// <summary>
/// Exposes Prometheus metrics for monitoring.
/// Aligns with modern observability standards.
/// </summary>
public sealed class PrometheusMetrics : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Stats _stats;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _serveTask;

    public PrometheusMetrics(Stats stats, ILogger logger, int metricsPort = 9090)
    {
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{metricsPort}/");
    }

    /// <summary>
    /// Starts the metrics server asynchronously.
    /// </summary>
    public async Task StartAsync(CancellationToken token = default)
    {
        _listener.Start();
        _serveTask = Task.Run(() => ServeLoopAsync(token), token);
        _logger.LogInfo($"Prometheus metrics server started on port 9090");
    }

    /// <summary>
    /// Main serve loop for handling metrics requests.
    /// </summary>
    private async Task ServeLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var ctx = await _listener.GetContextAsync().ConfigureAwait(false);

                if (ctx.Request.Url?.AbsolutePath == "/metrics")
                {
                    await HandleMetricsRequestAsync(ctx).ConfigureAwait(false);
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
            _logger.LogError($"Metrics server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles a Prometheus metrics request.
    /// </summary>
    private async Task HandleMetricsRequestAsync(HttpListenerContext ctx)
    {
        var metrics = BuildMetricsOutput();
        var buffer = Encoding.UTF8.GetBytes(metrics);

        ctx.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentLength64 = buffer.Length;

        await ctx.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
        ctx.Response.Close();
    }

    /// <summary>
    /// Builds Prometheus metrics output in text format.
    /// </summary>
    private string BuildMetricsOutput()
    {
        var sb = new StringBuilder();

        // Connection metrics
        sb.AppendLine("# HELP tinyproxy_net_connections_total");
        sb.AppendLine("# TYPE tinyproxy_net_connections_total gauge");
        sb.AppendLine($"tinyproxy_net_connections_total {_stats.ActiveConnections}");

        sb.AppendLine();
        sb.AppendLine("# HELP tinyproxy_net_requests_total");
        sb.AppendLine("# TYPE tinyproxy_net_requests_total counter");
        sb.AppendLine($"tinyproxy_net_requests_total {_stats.TotalRequests}");

        sb.AppendLine();
        sb.AppendLine("# HELP tinyproxy_net_bytes_sent_total");
        sb.AppendLine("# TYPE tinyproxy_net_bytes_sent_total counter");
        sb.AppendLine($"tinyproxy_net_bytes_sent_total {_stats.TotalBytesSent}");

        sb.AppendLine();
        sb.AppendLine("# HELP tinyproxy_net_bytes_received_total");
        sb.AppendLine("# TYPE tinyproxy_net_bytes_received_total counter");
        sb.AppendLine($"tinyproxy_net_bytes_received_total {_stats.TotalBytesReceived}");

        sb.AppendLine();
        sb.AppendLine("# HELP tinyproxy_net_failed_requests_total");
        sb.AppendLine("# TYPE tinyproxy_net_failed_requests_total counter");
        sb.AppendLine($"tinyproxy_net_failed_requests_total {_stats.FailedRequests}");

        sb.AppendLine();
        sb.AppendLine("# HELP tinyproxy_net_denied_requests_total");
        sb.AppendLine("# TYPE tinyproxy_net_denied_requests_total counter");
        sb.AppendLine($"tinyproxy_net_denied_requests_total {_stats.DeniedRequests}");

        return sb.ToString();
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