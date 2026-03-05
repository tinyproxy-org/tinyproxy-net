namespace TinyProxy.Metrics;

/// <summary>
/// Exposes Prometheus metrics for monitoring.
/// </summary>
public sealed class PrometheusMetrics : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Stats _stats;
    private readonly ILogger _logger;
    private readonly int _metricsPort;
    private Task? _serveTask;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PrometheusMetrics"/> class.
    /// </summary>
    public PrometheusMetrics(Stats stats, ILogger logger, int metricsPort = 9090)
    {
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (metricsPort <= 0 || metricsPort > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(metricsPort), metricsPort, "Port must be between 1 and 65535.");
        _metricsPort = metricsPort;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{metricsPort}/");
    }

    /// <summary>
    /// Starts the metrics server asynchronously.
    /// </summary>
    public Task StartAsync(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        _listener.Start();
        _serveTask = Task.Run(() => ServeLoopAsync(token));
        _logger.LogInfo($"Prometheus metrics server started on port {_metricsPort}");
        return Task.CompletedTask;
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
