namespace TinyProxy;

class Program
{
    static void Main(string[] args)
    {
        Child.ListenSockets();
        Child.MainLoop();
    }
}
