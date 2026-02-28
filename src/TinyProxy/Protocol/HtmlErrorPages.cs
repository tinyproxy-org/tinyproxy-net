using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TinyProxy.Config;
using TinyProxy.Core;

namespace TinyProxy.Protocol;

/// <summary>
/// Generates HTML error pages for proxy responses.
/// Supports custom error pages from file system.
/// Thread-safe implementation with immutable configuration.
/// </summary>
public static class HtmlErrorPages
{
    private static volatile string? _errorPagesDirectory;
    private static volatile Dictionary<int, string>? _customPages;
    private static readonly ConcurrentDictionary<int, string> s_cachedErrorPages = new();
    private static readonly object _initLock = new();

    /// <summary>
    /// Initializes the error page system with configuration.
    /// Must be called before using error pages.
    /// Thread-safe: can be called from config hot-reload.
    /// </summary>
    public static void Initialize(Configuration config)
    {
        lock (_initLock)
        {
            _errorPagesDirectory = config.ErrorPagesDirectory;
            _customPages = config.CustomErrorPages != null
                ? new Dictionary<int, string>(config.CustomErrorPages)
                : null;
            s_cachedErrorPages.Clear();
        }
    }

    private static string GetErrorContent(int statusCode, string status, string message)
    {
        if (s_cachedErrorPages.TryGetValue(statusCode, out var cachedPage)) return cachedPage;

        if (TryLoadErrorPage(statusCode, out var pageContent))
        {
            s_cachedErrorPages.TryAdd(statusCode, pageContent);
            return pageContent;
        }

        return GetDefaultErrorPage(statusCode, status, message);
    }

    private static bool TryLoadErrorPage(int statusCode, out string content)
    {
        content = string.Empty;

        // Priority 1: Use custom page from CustomErrorPages dictionary.
        if (_customPages != null &&
            _customPages.TryGetValue(statusCode, out var customPath) &&
            File.Exists(customPath))
        {
            content = File.ReadAllText(customPath);
            return true;
        }

        // Priority 2: Try to load from ErrorPagesDirectory.
        if (!string.IsNullOrEmpty(_errorPagesDirectory))
        {
            var pagePath = Path.Combine(_errorPagesDirectory, $"{statusCode}.html");
            if (File.Exists(pagePath))
            {
                content = File.ReadAllText(pagePath);
                return true;
            }
        }

        return false;
    }

    private static string GetDefaultErrorPage(int code, string status, string message)
    {
        return $@"
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
    }

    public static async Task SendErrorAsync(
        Socket socket,
        int code,
        string status,
        string message,
        CancellationToken token = default)
    {
        var html = GetErrorContent(code, status, message);
        var response = $"HTTP/1.1 {code} {status}\r\n" +
                       $"Content-Type: text/html; charset=utf-8\r\n" +
                       $"Content-Length: {Encoding.UTF8.GetByteCount(html)}\r\n" +
                       $"Connection: close\r\n" +
                       $"\r\n{html}";

        var buffer = Encoding.UTF8.GetBytes(response);
        await socket.SendAllAsync(buffer, token).ConfigureAwait(false);
    }

    public static Task BadRequestAsync(Socket socket, string message, CancellationToken token = default)
    {
        return SendErrorAsync(socket, 400, "Bad Request", message, token);
    }

    public static Task ForbiddenAsync(Socket socket, string message, CancellationToken token = default)
    {
        return SendErrorAsync(socket, 403, "Forbidden", message, token);
    }

    public static Task NotFoundAsync(Socket socket, string message, CancellationToken token = default)
    {
        return SendErrorAsync(socket, 404, "Not Found", message, token);
    }

    public static Task BadGatewayAsync(Socket socket, string message, CancellationToken token = default)
    {
        return SendErrorAsync(socket, 502, "Bad Gateway", message, token);
    }

    public static Task ServiceUnavailableAsync(Socket socket, string message, CancellationToken token = default)
    {
        return SendErrorAsync(socket, 503, "Service Unavailable", message, token);
    }

    public static Task GatewayTimeoutAsync(Socket socket, string message, CancellationToken token = default)
    {
        return SendErrorAsync(socket, 504, "Gateway Timeout", message, token);
    }

    public static async Task UnauthorizedAsync(Socket socket, string realm = "TinyProxy", CancellationToken token = default)
    {
        var html = GetErrorContent(401, "Unauthorized", "This server could not verify that you are authorized to access the document requested.");
        var response = $"HTTP/1.1 401 Unauthorized\r\n" +
                       $"Content-Type: text/html; charset=utf-8\r\n" +
                       $"Content-Length: {Encoding.UTF8.GetByteCount(html)}\r\n" +
                       $"WWW-Authenticate: Basic realm=\"{realm}\"\r\n" +
                       $"Connection: close\r\n" +
                       $"\r\n{html}";

        var buffer = Encoding.UTF8.GetBytes(response);
        await socket.SendAllAsync(buffer, token).ConfigureAwait(false);
    }

    public static async Task ProxyAuthenticationRequiredAsync(Socket socket, string realm = "TinyProxy", CancellationToken token = default)
    {
        var html = GetErrorContent(407, "Proxy Authentication Required", "This server could not verify that you are authorized to access the document requested.");
        var response = $"HTTP/1.1 407 Proxy Authentication Required\r\n" +
                       $"Content-Type: text/html; charset=utf-8\r\n" +
                       $"Content-Length: {Encoding.UTF8.GetByteCount(html)}\r\n" +
                       $"Proxy-Authenticate: Basic realm=\"{realm}\"\r\n" +
                       $"Connection: close\r\n" +
                       $"\r\n{html}";

        var buffer = Encoding.UTF8.GetBytes(response);
        await socket.SendAllAsync(buffer, token).ConfigureAwait(false);
    }
}
