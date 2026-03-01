namespace TinyProxy.Protocol;

/// <summary>
/// Handles reverse proxy mode.
/// Reverse proxy allows mapping local paths to upstream servers,
/// making the proxy act as a reverse proxy for specific paths.
/// </summary>
public sealed class ReverseProxy
{
    private const string ReverseCookieName = "yummy_magical_cookie";

    private readonly ILogger _logger;
    private readonly Configuration _config;
    private readonly Stats _stats;
    private readonly AccessLogger _accessLogger;
    private readonly string _clientIp;

    public enum RewriteStatus
    {
        NotMatched,
        ResponseSent,
        Rewritten
    }

    /// <summary>
    /// Represents the result of a reverse-proxy rewrite attempt.
    /// </summary>
    /// <param name="Status">The rewrite decision status.</param>
    /// <param name="Request">The rewritten or original request.</param>
    public readonly record struct RewriteResult(RewriteStatus Status, HttpRequest Request);

    /// <summary>
    /// Initializes a new instance of the <see cref="ReverseProxy"/> class.
    /// </summary>
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
    /// </summary>
    public async ValueTask<bool> TryHandleAsync(
        Connection connection,
        HttpRequest request,
        CancellationToken token)
    {
        var rewriteResult = await TryRewriteAsync(connection, request, token).ConfigureAwait(false);
        if (rewriteResult.Status == RewriteStatus.NotMatched) return false;
        if (rewriteResult.Status == RewriteStatus.ResponseSent) return true;

        var forwarder = new HttpForwarder(_logger, _config, _stats, _accessLogger, _clientIp);
        await forwarder.ForwardAsync(connection, rewriteResult.Request, token).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Tries to rewrite reverse proxy requests without forwarding.
    /// Allows callers to apply additional policies (for example URL filtering)
    /// after rewrite and before outbound forwarding.
    /// </summary>
    public async ValueTask<RewriteResult> TryRewriteAsync(
        Connection connection,
        HttpRequest request,
        CancellationToken token)
    {
        if (string.IsNullOrEmpty(request.Uri) || request.Uri[0] != '/')
            return new RewriteResult(RewriteStatus.NotMatched, request);

        var matchedPath = FindReversePath(request.Uri);
        var matchedByCookie = false;
        if (matchedPath == null)
        {
            if (_config.ReverseMagicEnabled)
            {
                matchedPath = FindReversePathByCookie(request.Headers);
                matchedByCookie = matchedPath != null;
            }

            if (matchedPath == null)
                return new RewriteResult(RewriteStatus.NotMatched, request);
        }

        if (!matchedByCookie && request.Uri.Length == matchedPath.Path.Length - 1)
        {
            await SendRedirectAsync(connection.ClientSocket, matchedPath.Path, token).ConfigureAwait(false);
            _accessLogger.LogAccess(_clientIp,
                request.GetMethodToken(),
                request.Uri,
                request.Version,
                301,
                0);
            return new RewriteResult(RewriteStatus.ResponseSent, request);
        }

        var rewrittenUrl = matchedByCookie
            ? RewriteUrlFromMagicCookie(request.Uri, matchedPath)
            : RewriteUrl(request.Uri, matchedPath);

        if (_config.Verbose) _logger.LogInfo($"Reverse proxy: {request.Uri} -> {rewrittenUrl}");

        var modifiedRequest = request.WithUri(rewrittenUrl);
        if (_config.ReverseMagicEnabled)
            modifiedRequest = modifiedRequest.WithReverseMagicCookiePath(matchedPath.Path);

        return new RewriteResult(RewriteStatus.Rewritten, modifiedRequest);
    }

    /// <summary>
    /// Finds the reverse path configuration that matches the request URI.
    /// </summary>
    private ReversePathConfig? FindReversePath(string uri)
    {
        foreach (var reversePath in _config.ReversePaths)
        {
            var path = reversePath.Path;
            var uriLen = uri.Length;
            var pathLen = path.Length;
            int compareLength;

            if (uriLen == pathLen - 1)
                compareLength = uriLen;
            else if (pathLen <= uriLen)
                compareLength = pathLen;
            else
                continue;

            if (uri.AsSpan(0, compareLength).SequenceEqual(path.AsSpan(0, compareLength)))
                return reversePath;
        }

        return null;
    }

    /// <summary>
    /// Finds the reverse path configuration using the magic cookie.
    /// </summary>
    private ReversePathConfig? FindReversePathByCookie(IDictionary<string, ReadOnlySequence<byte>> headers)
    {
        foreach (var kvp in headers)
            if (string.Equals(kvp.Key, "Cookie", StringComparison.OrdinalIgnoreCase))
            {
                var span = kvp.Value.IsSingleSegment ? kvp.Value.FirstSpan : kvp.Value.ToArray();
                var cookie = Encoding.ASCII.GetString(span);

                var pattern = $"{ReverseCookieName}=";
                var idx = cookie.IndexOf(pattern, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var startIdx = idx + pattern.Length;
                    var cookieValue = cookie.Substring(startIdx).TrimStart();
                    var reversePath = FindReversePath(cookieValue);
                    if (reversePath != null)
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
    /// </summary>
    private static string RewriteUrl(string uri, ReversePathConfig reversePath)
    {
        var remainingPath = uri.Substring(reversePath.Path.Length);

        // Concatenate reverse target URL and the unmatched URI remainder.
        return $"{reversePath.Url}{remainingPath}";
    }

    private static string RewriteUrlFromMagicCookie(string uri, ReversePathConfig reversePath)
    {
        // In magic-cookie mode, strip the leading slash before concatenation.
        var remainingPath = uri.Length > 0 ? uri[1..] : string.Empty;
        return $"{reversePath.Url}{remainingPath}";
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
