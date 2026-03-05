namespace TinyProxy.Tests.Protocol;

/// <summary>
/// Integration-style tests for HtmlErrorPages using real loopback sockets.
/// </summary>
public class HtmlErrorPagesTests : IDisposable
{
    private readonly string _tempDir;

    public HtmlErrorPagesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tinyproxy-error-pages-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
                // Ignore cleanup errors in tests.
            }
        }
    }

    [Fact]
    public async Task SendErrorAsync_UsesCustomPageAndCachesUntilReinitialized()
    {
        var custom404Path = Path.Combine(_tempDir, "custom-404.html");
        File.WriteAllText(custom404Path, "<h1>Custom V1</h1>");

        var config = new Configuration
        {
            CustomErrorPages = new Dictionary<int, string>
            {
                [404] = custom404Path
            }
        };

        HtmlErrorPages.Initialize(config);

        var firstResponse = await SendAndReceiveAsync(
            socket => HtmlErrorPages.SendErrorAsync(socket, 404, "Not Found", "ignored"),
            CancellationToken.None);
        Assert.Contains("<h1>Custom V1</h1>", firstResponse);

        File.WriteAllText(custom404Path, "<h1>Custom V2</h1>");

        var cachedResponse = await SendAndReceiveAsync(
            socket => HtmlErrorPages.SendErrorAsync(socket, 404, "Not Found", "ignored"),
            CancellationToken.None);
        Assert.Contains("<h1>Custom V1</h1>", cachedResponse);
        Assert.DoesNotContain("<h1>Custom V2</h1>", cachedResponse);

        HtmlErrorPages.Initialize(config);
        var refreshedResponse = await SendAndReceiveAsync(
            socket => HtmlErrorPages.SendErrorAsync(socket, 404, "Not Found", "ignored"),
            CancellationToken.None);
        Assert.Contains("<h1>Custom V2</h1>", refreshedResponse);
    }

    [Fact]
    public async Task SendErrorAsync_FallsBackToDirectoryThenBuiltin()
    {
        File.WriteAllText(Path.Combine(_tempDir, "403.html"), "<h1>Directory 403</h1>");
        var config = new Configuration { ErrorPagesDirectory = _tempDir };
        HtmlErrorPages.Initialize(config);

        var directoryResponse = await SendAndReceiveAsync(
            socket => HtmlErrorPages.SendErrorAsync(socket, 403, "Forbidden", "ignored"),
            CancellationToken.None);
        Assert.Contains("<h1>Directory 403</h1>", directoryResponse);

        var builtinResponse = await SendAndReceiveAsync(
            socket => HtmlErrorPages.SendErrorAsync(socket, 502, "Bad Gateway", "Upstream failed"),
            CancellationToken.None);
        Assert.Contains("502 Bad Gateway", builtinResponse);
        Assert.Contains("Upstream failed", builtinResponse);
    }

    [Fact]
    public async Task ProxyAuthenticationRequiredAsync_IncludesAuthenticateHeader()
    {
        HtmlErrorPages.Initialize(new Configuration());

        var response = await SendAndReceiveAsync(
            socket => HtmlErrorPages.ProxyAuthenticationRequiredAsync(socket, "TestRealm"),
            CancellationToken.None);

        Assert.Contains("407 Proxy Authentication Required", response);
        Assert.Contains("Proxy-Authenticate: Basic realm=\"TestRealm\"", response);
    }

    [Fact]
    public async Task UnauthorizedAsync_IncludesWwwAuthenticateHeader()
    {
        HtmlErrorPages.Initialize(new Configuration());

        var response = await SendAndReceiveAsync(
            socket => HtmlErrorPages.UnauthorizedAsync(socket, "StatRealm"),
            CancellationToken.None);

        Assert.Contains("401 Unauthorized", response);
        Assert.Contains("WWW-Authenticate: Basic realm=\"StatRealm\"", response);
    }

    private static async Task<string> SendAndReceiveAsync(
        Func<Socket, Task> sendAction,
        CancellationToken token)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!, token).AsTask();

        using var server = await listener.AcceptAsync(token);
        await connectTask;

        await sendAction(server);
        server.Shutdown(SocketShutdown.Send);

        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        while (true)
        {
            var read = await client.ReceiveAsync(buffer, SocketFlags.None, token);
            if (read <= 0) break;
            ms.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
