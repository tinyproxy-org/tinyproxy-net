using System.Buffers;
using System.Net;
using System.Net.Sockets;
using TinyProxy.Config;

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
    public static async ValueTask SendAsync(
        this Socket socket,
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        int sent = 0;
        while (sent < buffer.Length)
        {
            var result = await socket.SendAsync(buffer.Slice(sent), SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (result == 0) throw new SocketException((int)SocketError.ConnectionReset);
            sent += result;
        }
    }

    /// <summary>
    /// Sends data to socket and returns number of bytes sent.
    /// </summary>
    public static async ValueTask<int> SendAndReturnAsync(
        this Socket socket,
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var result = await socket.SendAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
        return result;
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
        if (!config.BindSame)
        {
            return;
        }

        // Get the local endpoint of the client connection (the IP client connected to)
        if (clientSocket.LocalEndPoint is not IPEndPoint localEndPoint)
        {
            return;
        }

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

    /// <summary>
    /// Safe write - writes all data to socket, handling EINTR.
    /// Aligns with tinyproxy C's safe_write() from network.c.
    /// </summary>
    public static async ValueTask SafeWriteAsync(
        this Socket socket,
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        int bytesToSend = buffer.Length;
        int sent = 0;

        while (sent < bytesToSend)
        {
            var result = await socket.SendAsync(
                buffer.Slice(sent),
                SocketFlags.None,
                cancellationToken).ConfigureAwait(false);

            if (result < 0)
            {
                throw new SocketException((int)SocketError.ConnectionReset);
            }

            sent += result;
        }
    }

    /// <summary>
    /// Safe read - reads data from socket, handling EINTR.
    /// Aligns with tinyproxy C's safe_read() from network.c.
    /// </summary>
    public static async ValueTask<int> SafeReadAsync(
        this Socket socket,
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        int totalRead = 0;

        while (totalRead < buffer.Length)
        {
            var result = await socket.ReceiveAsync(
                buffer.Slice(totalRead),
                SocketFlags.None,
                cancellationToken).ConfigureAwait(false);

            if (result < 0)
            {
                throw new SocketException((int)SocketError.ConnectionReset);
            }

            if (result == 0)
            {
                // Connection closed
                break;
            }

            totalRead += result;
        }

        return totalRead;
    }

    /// <summary>
    /// Reads a line of text from socket.
    /// Aligns with tinyproxy C's readline() from network.c.
    /// </summary>
    public static async ValueTask<string?> ReadLineAsync(
        this Socket socket,
        CancellationToken cancellationToken = default)
    {
        var buffer = new System.Text.StringBuilder();
        var tempBuffer = new byte[1];

        while (true)
        {
            var read = await socket.ReceiveAsync(
                tempBuffer,
                SocketFlags.None,
                cancellationToken).ConfigureAwait(false);

            if (read <= 0)
            {
                return null;
            }

            char c = (char)tempBuffer[0];
            if (c == '\n')
            {
                break;
            }

            if (c != '\r')
            {
                buffer.Append(c);
            }
        }

        return buffer.ToString();
    }

    /// <summary>
    /// Reads a line of bytes from socket.
    /// Zero-allocation version using ArrayPool.
    /// </summary>
    public static async ValueTask<Memory<byte>?> ReadLineBytesAsync(
        this Socket socket,
        CancellationToken cancellationToken = default)
    {
        const int MaxLineLength = 8192;
        var buffer = ArrayPool<byte>.Shared.Rent(MaxLineLength);
        int pos = 0;

        try
        {
            while (pos < MaxLineLength)
            {
                var read = await socket.ReceiveAsync(
                    buffer.AsMemory(pos, 1),
                    SocketFlags.None,
                    cancellationToken).ConfigureAwait(false);

                if (read <= 0)
                {
                    return null;
                }

                if (buffer[pos] == '\n')
                {
                    return buffer.AsMemory(0, pos);
                }

                pos++;
            }

            throw new IOException("Line too long");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
