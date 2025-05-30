using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TinyProxy;

public class Child
{
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

        byte[] buf = new byte[1024];

        while (true)
        {
            try
            {
                var socket = ListeningSocket.Accept();
                
                Console.WriteLine($"client connected: {socket.RemoteEndPoint}");
                
                var len = socket.Receive(buf);
                
                Console.WriteLine($"client data: len = {len}, content = {Encoding.UTF8.GetString(buf[0..len])}");
                
                socket.Close();
                socket.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in MainLoop: {ex.Message}");
                break;
            }
        }
    }
}