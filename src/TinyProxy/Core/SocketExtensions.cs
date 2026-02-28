namespace TinyProxy.Core;

/// <summary>
/// Extension methods for Socket operations.
/// Aligns with tinyproxy C's sock.c
/// </summary>
public static class SocketExtensions
{
    /// <summary>
    /// Sends all data from buffer to socket.
    /// </summary>
    public static async ValueTask SendAllAsync(
        this Socket socket,
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var sent = 0;
        while (sent < buffer.Length)
        {
            var result = await socket.SendAsync(buffer.Slice(sent), SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (result == 0) throw new SocketException((int)SocketError.ConnectionReset);
            sent += result;
        }
    }

    /// <summary>
    /// Sends all bytes from a sequence to socket.
    /// </summary>
    public static async ValueTask SendAllAsync(
        this Socket socket,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        foreach (var segment in buffer)
        {
            if (segment.IsEmpty) continue;
            await socket.SendAllAsync(segment, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Receives exactly <paramref name="buffer"/> length bytes from socket.
    /// Throws if connection closes before the buffer is filled.
    /// </summary>
    public static async ValueTask ReceiveExactlyAsync(
        this Socket socket,
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var received = 0;
        while (received < buffer.Length)
        {
            var read = await socket.ReceiveAsync(
                buffer.Slice(received),
                SocketFlags.None,
                cancellationToken).ConfigureAwait(false);

            if (read == 0)
                throw new EndOfStreamException("Socket closed before receiving the expected number of bytes.");

            received += read;
        }
    }

    /// <summary>
    /// Connects to endpoint with timeout.
    /// Aligns with tinyproxy C's opensock().
    /// </summary>
    public static async Task ConnectAsync(
        this Socket socket,
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            await socket.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Connection to {host}:{port} timed out after {timeout}");
        }
    }

    /// <summary>
    /// Connects to endpoint with timeout and binds to a specific local address.
    /// Aligns with tinyproxy C's opensock() with bind_to parameter.
    /// </summary>
    public static async Task ConnectAndBindAsync(
        this Socket socket,
        string host,
        int port,
        TimeSpan timeout,
        string? bindAddress,
        CancellationToken cancellationToken = default)
    {
        // Bind to specific address if requested
        if (!string.IsNullOrEmpty(bindAddress))
        {
            var bindEndPoint = new IPEndPoint(IPAddress.Parse(bindAddress), 0);
            socket.Bind(bindEndPoint);
        }

        await socket.ConnectAsync(host, port, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Binds socket to the same IP as the incoming connection.
    /// This is useful for multi-homed servers.
    /// Aligns with tinyproxy C's bindsame functionality.
    /// </summary>
    public static void BindToSameIp(this Socket serverSocket, Socket clientSocket, Configuration config)
    {
        // Only bind if BindSame is enabled
        if (!config.BindSame) return;

        // Get the local endpoint of the client connection (the IP client connected to)
        if (clientSocket.LocalEndPoint is not IPEndPoint localEndPoint) return;

        try
        {
            // Bind the server socket to the same IP
            var bindEndPoint = new IPEndPoint(localEndPoint.Address, 0);
            serverSocket.Bind(bindEndPoint);
        }
        catch
        {
            // Silently fail if binding fails
        }
    }
}
