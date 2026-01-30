using System.Net.Sockets;
using System.Text;
using TinyProxy.Core;

namespace TinyProxy.Protocol;

/// <summary>
/// Generates HTML error pages for proxy responses.
/// </summary>
public static class HtmlErrorPages
{
    public static async Task SendErrorAsync(
        Socket socket,
        int code,
        string status,
        string message,
        CancellationToken token = default)
    {
        var html = $@"
<!DOCTYPE HTML PUBLIC ""-//W3C//DTD HTML 4.01//EN"" ""http://www.w3.org/TR/html4/strict.dtd"">
<html>
<head>
    <meta http-equiv=""Content-Type"" content=""text/html; charset=utf-8"">
    <title>{code} {status}</title>
    <style type=""text/css"">
        body {{ font-family: sans-serif; margin: 40px; }}
        h1 {{ color: #c00; }}
        hr {{ border: none; border-top: 1px solid #ccc; margin: 20px 0; }}
        address {{ font-size: smaller; color: #888; }}
    </style>
</head>
<body>
    <h1>{code} {status}</h1>
    <p>{System.Net.WebUtility.HtmlEncode(message)}</p>
    <hr>
    <address>TinyProxy.NET</address>
</body>
</html>";

        var response = $"HTTP/1.1 {code} {status}\r\n" +
                       $"Content-Type: text/html; charset=utf-8\r\n" +
                       $"Content-Length: {Encoding.UTF8.GetByteCount(html)}\r\n" +
                       $"Connection: close\r\n" +
                       $"\r\n{html}";

        var buffer = Encoding.UTF8.GetBytes(response);
        await socket.SendAsync(buffer, SocketFlags.None, token).ConfigureAwait(false);
    }

    public static Task BadRequestAsync(Socket socket, string message, CancellationToken token = default)
        => SendErrorAsync(socket, 400, "Bad Request", message, token);

    public static Task ForbiddenAsync(Socket socket, string message, CancellationToken token = default)
        => SendErrorAsync(socket, 403, "Forbidden", message, token);

    public static Task NotFoundAsync(Socket socket, string message, CancellationToken token = default)
        => SendErrorAsync(socket, 404, "Not Found", message, token);

    public static Task BadGatewayAsync(Socket socket, string message, CancellationToken token = default)
        => SendErrorAsync(socket, 502, "Bad Gateway", message, token);

    public static Task ServiceUnavailableAsync(Socket socket, string message, CancellationToken token = default)
        => SendErrorAsync(socket, 503, "Service Unavailable", message, token);

    public static Task GatewayTimeoutAsync(Socket socket, string message, CancellationToken token = default)
        => SendErrorAsync(socket, 504, "Gateway Timeout", message, token);

    public static async Task ProxyAuthenticationRequiredAsync(Socket socket, string realm = "TinyProxy", CancellationToken token = default)
    {
        var html = $@"
<!DOCTYPE HTML PUBLIC ""-//W3C//DTD HTML 4.01//EN"" ""http://www.w3.org/TR/html4/strict.dtd"">
<html>
<head>
    <meta http-equiv=""Content-Type"" content=""text/html; charset=utf-8"">
    <title>407 Proxy Authentication Required</title>
    <style type=""text/css"">
        body {{ font-family: sans-serif; margin: 40px; }}
        h1 {{ color: #c00; }}
    </style>
</head>
<body>
    <h1>407 Proxy Authentication Required</h1>
    <p>This server could not verify that you are authorized to access the document requested.</p>
</body>
</html>";

        var response = $"HTTP/1.1 407 Proxy Authentication Required\r\n" +
                       $"Content-Type: text/html; charset=utf-8\r\n" +
                       $"Content-Length: {Encoding.UTF8.GetByteCount(html)}\r\n" +
                       $"Proxy-Authenticate: Basic realm=\"{realm}\"\r\n" +
                       $"Connection: close\r\n" +
                       $"\r\n{html}";

        var buffer = Encoding.UTF8.GetBytes(response);
        await socket.SendAsync(buffer, SocketFlags.None, token).ConfigureAwait(false);
    }
}
