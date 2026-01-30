using System.Net;
using System.Net.Sockets;
using TinyProxy.Config;

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
    // Use fixed-size array instead of List to reduce allocations
    private readonly Task?[] _activeConnectionTasks;
    private int _activeTaskCount = 0;
    private bool _disposed;
    private Task? _runTask;

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

        // Pre-allocate array for max concurrent connections
        _activeConnectionTasks = new Task?[config.MaxClients];
    }

    /// <summary>
    /// Starts accepting connections asynchronously.
    /// </summary>
    public void Start()
    {
        _runTask = RunAsync();
    }

    private async Task RunAsync()
    {
        var token = _cts.Token;

        try
        {
            _logger.LogInfo($"EventLoop started on {_listener.LocalEndPoint} (max clients: {_connectionManager.MaxClients})");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var socket = await _listener.AcceptAsync(token).ConfigureAwait(false);

                    // Try to acquire a connection slot
                    var slot = await _connectionManager.TryAcquireSlotAsync(null, token).ConfigureAwait(false);
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

                    if (_config.Verbose)
                    {
                        _logger.LogConnect($"Connection from {socket.RemoteEndPoint} (active: {_connectionManager.ActiveCount})");
                    }

                    // Handle connection with slot management
                    var taskIndex = Interlocked.Increment(ref _activeTaskCount) - 1;
                    Task? task = null;
                    task = Task.Run(async () =>
                    {
                        try
                        {
                            await _handleConnection(socket);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Connection handler error: {ex.Message}");
                        }
                        finally
                        {
                            socket.Dispose();
                            slot.Dispose();

                            // Clear from tracking array
                            if (taskIndex >= 0 && taskIndex < _activeConnectionTasks.Length)
                            {
                                _activeConnectionTasks[taskIndex] = null;
                            }
                        }
                    }, token);

                    // Track task for graceful shutdown
                    if (taskIndex >= 0 && taskIndex < _activeConnectionTasks.Length)
                    {
                        _activeConnectionTasks[taskIndex] = task;
                    }
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
        }
        finally
        {
            _completionSource.TrySetResult();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Cancel the accept loop first
        _cts.Cancel();

        // Close listener to unblock AcceptAsync immediately
        try
        {
            _listener.Dispose();
        }
        catch
        {
            // Ignore
        }

        // Wait for accept loop to complete (should be fast after listener closed)
        try
        {
            _runTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore timeout
        }

        // Don't wait for active connections - they will be aborted
        // The OS will clean up sockets when process exits
        _cts.Dispose();
    }
}
