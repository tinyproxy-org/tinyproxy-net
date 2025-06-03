using System.Net;
using System.Net.Sockets;

namespace TinyProxy;

public class Child
{
    private static void ChildThread(object? data)
    {
        if (data is null)
        {
            Console.WriteLine("Error: Thread payload is null.");
            return;
        }

        if (data is not Socket socket)
        {
            Console.WriteLine("Error: Thread payload data type expects to be Socket.");
            return;
        }

        var buf = new byte[1024];

        var len = socket.Receive(buf);

        Console.WriteLine($"client data: len = {len}");

        socket.Close();
        socket.Dispose();
    }

    private static Socket? ListeningSocket { get; set; }

    public static void ListenSockets(string address = "127.0.0.1", ushort port = 9999)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Parse(address), port));
        socket.Listen(1024);
        ListeningSocket = socket;
    }

    public static void MainLoop()
    {
        if (ListeningSocket == null)
        {
            throw new InvalidOperationException("Listening socket is not initialized. Call ListenSockets() first.");
        }

        while (true)
        {
            try
            {
                var socket = ListeningSocket.Accept();

                Console.WriteLine($"client connected: {socket.RemoteEndPoint}");

                var thread = new Thread(ChildThread);
                thread.Start(socket);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in MainLoop: {ex.Message}");
                break;
            }
        }
    }
}