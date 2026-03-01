using System.Collections.Concurrent;

namespace TinyProxy.Core;

/// <summary>
/// Socket event loop that listens for incoming connections.
/// </summary>
public sealed class EventLoop : IDisposable
{
    private readonly Socket _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _completionSource = new();
    private readonly Func<Socket, ValueTask> _handleConnection;
    private readonly ILogger _logger;
    private readonly ConnectionManager _connectionManager;

    private readonly Configuration _config;
    private readonly ConcurrentDictionary<int, Task> _activeConnectionTasks = new();
    private int _nextTaskId;
    private bool _disposed;
    private Task? _runTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventLoop"/> class.
    /// </summary>
    public EventLoop(
        IPAddress address,
        int port,
        Func<Socket, ValueTask> handleConnection,
        ILogger logger,
        ConnectionManager connectionManager,
        Configuration config)
    {
        _handleConnection = handleConnection ?? throw new ArgumentNullException(nameof(handleConnection));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _config = config ?? throw new ArgumentNullException(nameof(config));

        _listener = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(address, port));
        _listener.Listen();
    }

    /// <summary>
    /// Starts accepting connections asynchronously.
    /// </summary>
    public void Start()
    {
        _runTask = RunAsync();
    }

    /// <summary>
    /// Runs a connection handler task with proper cleanup.
    /// </summary>
    private async Task RunConnectionAsync(Socket socket, ConnectionSlot slot, CancellationToken token)
    {
        try
        {
            await _handleConnection(socket).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Connection handler error: {ex.Message}");
        }
        finally
        {
            socket.Dispose();
            slot.Dispose();
        }
    }

    private async Task RunAsync()
    {
        var token = _cts.Token;

        try
        {
            _logger.LogInfo($"EventLoop started on {_listener.LocalEndPoint} (max clients: {_connectionManager.MaxClients})");

            while (!token.IsCancellationRequested)
                try
                {
                    var socket = await _listener.AcceptAsync(token).ConfigureAwait(false);
                    var clientIp = GetClientIp(socket);

                    var slot = await _connectionManager.TryAcquireSlotAsync(clientIp, token).ConfigureAwait(false);
                    if (slot == null)
                    {
                        _logger.LogWarning($"Connection limit reached, rejecting {socket.RemoteEndPoint}");
                        await Protocol.HtmlErrorPages.ServiceUnavailableAsync(
                            socket,
                            "Too many connections",
                            token).ConfigureAwait(false);
                        socket.Dispose();
                        continue;
                    }

                    if (_config.Verbose) _logger.LogConnect($"Connection from {socket.RemoteEndPoint} (active: {_connectionManager.ActiveCount})");

                    var taskId = Interlocked.Increment(ref _nextTaskId);
                    var task = RunConnectionAsync(socket, slot, token);

                    _activeConnectionTasks[taskId] = task;
                    _ = task.ContinueWith(
                        _ => { _activeConnectionTasks.TryRemove(taskId, out var removedTask); },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Accept error: {ex.Message}");
                }
        }
        finally
        {
            _completionSource.TrySetResult();
        }
    }

    /// <summary>
    /// Releases the resources used by this instance.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        try
        {
            _listener.Dispose();
        }
        catch
        {
            // Ignore
        }

        try
        {
            _runTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore timeout
        }

        _cts.Dispose();
    }

    private static string? GetClientIp(Socket socket)
    {
        return socket.RemoteEndPoint switch
        {
            IPEndPoint ip => ip.Address.ToString(),
            DnsEndPoint dns => dns.Host,
            _ => null
        };
    }
}