using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TinyProxy.Config;
using TinyProxy.Core;
using TinyProxy.Logging;
using TinyProxy.Metrics;
using TinyProxy.Protocol.Http;

namespace TinyProxy.Protocol;

/// <summary>
/// Handles reverse proxy mode.
/// Aligns with tinyproxy C's reverse-proxy.c
///
/// Reverse proxy allows mapping local paths to upstream servers,
/// making the proxy act as a reverse proxy for specific paths.
/// </summary>
public sealed class ReverseProxy
{
    private const string ReverseCookieName = "RPLPATH"; // Aligns with tinyproxy C's REVERSE_COOKIE

    private readonly ILogger _logger;
    private readonly Configuration _config;
    private readonly Stats _stats;
    private readonly AccessLogger _accessLogger;
    private readonly string _clientIp;

    public ReverseProxy(
        ILogger logger,
        Configuration config,
        Stats stats,
        AccessLogger accessLogger,
        string clientIp)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _accessLogger = accessLogger ?? throw new ArgumentNullException(nameof(accessLogger));
        _clientIp = clientIp ?? "unknown";
    }

    /// <summary>
    /// Tries to handle the request as a reverse proxy request.
    /// Returns true if the request was handled (matches a reverse path), false otherwise.
    /// Aligns with tinyproxy C's reverse_rewrite_url().
    /// </summary>
    public async ValueTask<bool> TryHandleAsync(
        Connection connection,
        HttpRequest request,
        CancellationToken token)
    {
        // Reverse proxy requests must start with /
        if (string.IsNullOrEmpty(request.Uri) || request.Uri[0] != '/') return false;

        // Find matching reverse path
        var matchedPath = FindReversePath(request.Uri);
        if (matchedPath == null)
        {
            // Try magic cookie if enabled
            if (_config.ReverseMagicEnabled) matchedPath = FindReversePathByCookie(request.Headers);

            if (matchedPath == null) return false;
        }

        // Rewrite the URL
        var rewrittenUrl = RewriteUrl(request.Uri, matchedPath);
        if (rewrittenUrl == null) return false;

        if (_config.Verbose) _logger.LogInfo($"Reverse proxy: {request.Uri} -> {rewrittenUrl}");

        // Create a modified request with the rewritten URL
        var modifiedRequest = request.WithUri(rewrittenUrl);

        // Check if we need to redirect (path without trailing slash)
        if (request.Uri.Length == matchedPath.Path.Length - 1)
        {
            // Redirect to add trailing slash (aligns with tinyproxy C behavior)
            await SendRedirectAsync(connection.ClientSocket, matchedPath.Path, token);
            _accessLogger.LogAccess(_clientIp,
                request.GetMethodToken(),
                request.Uri,
                request.Version,
                301,
                0);
            return true;
        }

        // Forward the rewritten request
        var forwarder = new HttpForwarder(_logger, _config, _stats, _accessLogger, _clientIp);
        await forwarder.ForwardAsync(connection, modifiedRequest, token);

        return true;
    }

    /// <summary>
    /// Finds the reverse path configuration that matches the request URI.
    /// Aligns with tinyproxy C's reversepath_get().
    /// </summary>
    private ReversePathConfig? FindReversePath(string uri)
    {
        foreach (var reversePath in _config.ReversePaths)
        {
            var path = reversePath.Path;
            var uriLen = uri.Length;
            var pathLen = path.Length;

            // Check if URI matches the reverse path
            // URI can be: exact match, path prefix, or one char shorter (missing trailing slash)
            if ((uriLen == pathLen - 1 || uriLen >= pathLen || pathLen <= uriLen) &&
                uri.StartsWith(path, StringComparison.Ordinal))
                return reversePath;
        }

        return null;
    }

    /// <summary>
    /// Finds the reverse path configuration using the magic cookie.
    /// Aligns with tinyproxy C's reversemagic handling.
    /// </summary>
    private ReversePathConfig? FindReversePathByCookie(IDictionary<string, ReadOnlySequence<byte>> headers)
    {
        foreach (var kvp in headers)
            if (string.Equals(kvp.Key, "Cookie", StringComparison.OrdinalIgnoreCase))
            {
                var span = kvp.Value.IsSingleSegment ? kvp.Value.FirstSpan : kvp.Value.ToArray();
                var cookie = Encoding.ASCII.GetString(span);

                // Look for RPLPATH=path pattern
                var pattern = $"{ReverseCookieName}=";
                var idx = cookie.IndexOf(pattern, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var startIdx = idx + pattern.Length;
                    var endIdx = cookie.IndexOf(';', startIdx);
                    if (endIdx < 0) endIdx = cookie.Length;

                    var cookieValue = cookie.Substring(startIdx, endIdx - startIdx).Trim();

                    // Find matching reverse path by cookie value
                    foreach (var reversePath in _config.ReversePaths)
                        if (cookieValue == reversePath.Path)
                        {
                            if (_config.Verbose) _logger.LogInfo($"Reverse magic cookie says: {reversePath.Path}");
                            return reversePath;
                        }
                }

                break;
            }

        return null;
    }

    /// <summary>
    /// Rewrites the request URL using the reverse path configuration.
    /// Aligns with tinyproxy C's reverse_rewrite_url().
    /// </summary>
    private string? RewriteUrl(string uri, ReversePathConfig reversePath)
    {
        var pathLen = reversePath.Path.Length;
        var uriLen = uri.Length;

        // If the reverse path is longer than the URI, redirect to add trailing slash
        if (pathLen > uriLen) return null; // Signal to redirect

        // Strip the reverse path prefix from the URI and append to upstream URL
        var remainingPath = uri.Substring(pathLen);
        var upstreamUrl = reversePath.Url.TrimEnd('/');

        return $"{upstreamUrl}{remainingPath}";
    }

    /// <summary>
    /// Sends a redirect response to add the trailing slash.
    /// </summary>
    private async ValueTask SendRedirectAsync(Socket socket, string path, CancellationToken token)
    {
        var html = $@"
<!DOCTYPE html>
<html>
<head><title>301 Moved Permanently</title></head>
<body>
<h1>Moved Permanently</h1>
<p>The resource has been moved to <a href=""{path}"">{path}</a>.</p>
</body>
</html>";

        var response = $"HTTP/1.1 301 Moved Permanently\r\n" +
                       $"Location: {path}\r\n" +
                       $"Content-Type: text/html\r\n" +
                       $"Content-Length: {Encoding.UTF8.GetByteCount(html)}\r\n" +
                       $"Connection: close\r\n" +
                       $"\r\n{html}";

        var buffer = Encoding.UTF8.GetBytes(response);
        await socket.SendAllAsync(buffer, token).ConfigureAwait(false);
    }
}
