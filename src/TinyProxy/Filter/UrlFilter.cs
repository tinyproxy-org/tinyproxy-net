using TinyProxy.Config;

namespace TinyProxy.Filter;

/// <summary>
/// URL filtering using regex patterns.
/// </summary>
public sealed class UrlFilter
{
    private readonly Configuration _config;

    public UrlFilter(Configuration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Checks if a URL is allowed based on filter rules.
    /// </summary>
    public bool IsAllowed(string url)
    {
        // If no filters configured, allow all
        if (_config.FilterRegexes.Count == 0)
        {
            return true;
        }

        // If default deny is set, deny all unless explicitly allowed
        if (_config.FilterDefaultDeny)
        {
            foreach (var regex in _config.FilterRegexes)
            {
                if (regex.IsMatch(url))
                {
                    return true; // Explicitly allowed
                }
            }
            return false; // Not explicitly allowed, deny
        }
        else
        {
            // Default allow, deny if matches any filter
            foreach (var regex in _config.FilterRegexes)
            {
                if (regex.IsMatch(url))
                {
                    return false; // Matched deny filter
                }
            }
            return true; // No filters matched, allow
        }
    }

    /// <summary>
    /// Checks if a request is allowed based on its URI.
    /// </summary>
    public bool IsRequestAllowed(TinyProxy.Protocol.Http.HttpRequest request)
    {
        // If URI already contains scheme (http:// or https://), just check it directly
        if (request.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            request.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return IsAllowed(request.Uri);
        }

        // Check path only, and if host is available, check full URL
        if (IsAllowed(request.Uri))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(request.Host))
        {
            return IsAllowed($"http://{request.Host}{request.Uri}");
        }

        return IsAllowed(request.Uri);
    }
}
