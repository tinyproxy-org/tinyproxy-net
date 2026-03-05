namespace TinyProxy.Tests.Metrics;

public sealed class EndpointLifecycleTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void PrometheusMetrics_Ctor_InvalidPort_ThrowsArgumentOutOfRangeException(int port)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new PrometheusMetrics(new Stats(), new NullLogger(), port));
        Assert.Equal("metricsPort", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void HealthCheck_Ctor_InvalidPort_ThrowsArgumentOutOfRangeException(int port)
    {
        var logger = new NullLogger();
        var config = new Configuration();
        var connectionManager = new ConnectionManager(config, logger);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new HealthCheck(config, connectionManager, logger, port));
        Assert.Equal("healthPort", exception.ParamName);
    }

    [Fact]
    public void PrometheusMetrics_Dispose_CanBeCalledMultipleTimes()
    {
        var metrics = new PrometheusMetrics(new Stats(), new NullLogger());

        metrics.Dispose();

        var exception = Record.Exception(metrics.Dispose);
        Assert.Null(exception);
    }

    [Fact]
    public void PrometheusMetrics_StartAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var metrics = new PrometheusMetrics(new Stats(), new NullLogger());
        metrics.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = metrics.StartAsync(); });
    }

    [Fact]
    public void PrometheusMetrics_StartAsync_WithCanceledToken_ThrowsOperationCanceledException()
    {
        var metrics = new PrometheusMetrics(new Stats(), new NullLogger());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => { _ = metrics.StartAsync(cts.Token); });
        metrics.Dispose();
    }

    [Fact]
    public void PrometheusMetrics_Dispose_DoesNotThrowWhenServeTaskIsStillRunning()
    {
        var metrics = new PrometheusMetrics(new Stats(), new NullLogger());
        var runningTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        SetServeTask(metrics, runningTask);

        var exception = Record.Exception(metrics.Dispose);

        Assert.Null(exception);
    }

    [Fact]
    public void HealthCheck_Dispose_CanBeCalledMultipleTimes()
    {
        var logger = new NullLogger();
        var config = new Configuration();
        var connectionManager = new ConnectionManager(config, logger);
        var healthCheck = new HealthCheck(config, connectionManager, logger);

        healthCheck.Dispose();

        var exception = Record.Exception(healthCheck.Dispose);
        Assert.Null(exception);
    }

    [Fact]
    public void HealthCheck_StartAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var logger = new NullLogger();
        var config = new Configuration();
        var connectionManager = new ConnectionManager(config, logger);
        var healthCheck = new HealthCheck(config, connectionManager, logger);
        healthCheck.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = healthCheck.StartAsync(); });
    }

    [Fact]
    public void HealthCheck_StartAsync_WithCanceledToken_ThrowsOperationCanceledException()
    {
        var logger = new NullLogger();
        var config = new Configuration();
        var connectionManager = new ConnectionManager(config, logger);
        var healthCheck = new HealthCheck(config, connectionManager, logger);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => { _ = healthCheck.StartAsync(cts.Token); });
        healthCheck.Dispose();
    }

    [Fact]
    public void HealthCheck_Dispose_DoesNotThrowWhenServeTaskIsStillRunning()
    {
        var logger = new NullLogger();
        var config = new Configuration();
        var connectionManager = new ConnectionManager(config, logger);
        var healthCheck = new HealthCheck(config, connectionManager, logger);
        var runningTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        SetServeTask(healthCheck, runningTask);

        var exception = Record.Exception(healthCheck.Dispose);

        Assert.Null(exception);
    }

    private static void SetServeTask<T>(T instance, Task serveTask)
    {
        var field = typeof(T).GetField("_serveTask", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, serveTask);
    }

    private sealed class NullLogger : ILogger
    {
        public void LogInfo(string message) { }
        public void LogError(string message) { }
        public void LogWarning(string message) { }
        public void LogConnect(string message) { }
        public void LogCritical(string message) { }
    }
}
