using System.Net;
using System.Net.Sockets;

namespace TinyProxy.Core;

/// <summary>
/// Extension methods for Socket operations.
/// </summary>
public static class SocketExtensions
{
    /// <summary>
    /// Sends all data from the buffer to the socket.
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
    /// Sends data to socket and returns the number of bytes sent.
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
}
