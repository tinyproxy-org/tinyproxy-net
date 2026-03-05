using System.IO;

namespace TinyProxy.Tests.Core;

public class SocketExtensionsTests
{
    [Fact]
    public async Task ReceiveExactlyAsync_ReadsAcrossMultipleSends()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!, CancellationToken.None).AsTask();

        using var server = await listener.AcceptAsync(CancellationToken.None);
        await connectTask;

        var sendTask = Task.Run(async () =>
        {
            await server.SendAllAsync(Encoding.ASCII.GetBytes("he"), CancellationToken.None);
            await Task.Delay(10);
            await server.SendAllAsync(Encoding.ASCII.GetBytes("llo"), CancellationToken.None);
            server.Shutdown(SocketShutdown.Send);
        });

        var buffer = new byte[5];
        await client.ReceiveExactlyAsync(buffer, CancellationToken.None);
        await sendTask;

        Assert.Equal("hello", Encoding.ASCII.GetString(buffer));
    }

    [Fact]
    public async Task ReceiveExactlyAsync_ThrowsWhenPeerClosesEarly()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!, CancellationToken.None).AsTask();

        using var server = await listener.AcceptAsync(CancellationToken.None);
        await connectTask;

        await server.SendAllAsync(Encoding.ASCII.GetBytes("hi"), CancellationToken.None);
        server.Shutdown(SocketShutdown.Both);

        var buffer = new byte[5];
        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await client.ReceiveExactlyAsync(buffer, CancellationToken.None));
    }
}
