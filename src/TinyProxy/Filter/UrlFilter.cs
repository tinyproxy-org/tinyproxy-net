using System.Text.RegularExpressions;

namespace TinyProxy.Filter;

/// <summary>
/// URL filtering using regex or glob patterns with ReDoS protection.
/// Aligns with tinyproxy C's filter.c implementation.
/// </summary>
public sealed class UrlFilter
{
    private readonly Configuration _config;
    private readonly ILogger _logger;
    private readonly List<FilterRule> _regexRules;
    private readonly List<FilterRule> _globRules;
    private const int RegexTimeoutMs = 5000; // 5 second timeout for regex matching
    public bool IsEnabled { get; }

    public UrlFilter(Configuration config, ILogger? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? new ConsoleLogger();
        IsEnabled = HasConfiguredFilter(config);

        // Parse filter regexes into rules
        _regexRules = new List<FilterRule>();
        _globRules = new List<FilterRule>();

        foreach (var pattern in config.FilterPatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;

            if (config.FilterUseGlob)
            {
                _globRules.Add(new FilterRule(pattern, FilterType.Glob));
                continue;
            }

            // Compile as regex with timeout protection
            try
            {
                var options = config.FilterCaseSensitive
                    ? RegexOptions.None
                    : RegexOptions.IgnoreCase;

                var regex = new Regex(pattern, options | RegexOptions.Compiled, TimeSpan.FromMilliseconds(RegexTimeoutMs));
                _regexRules.Add(new FilterRule(pattern, FilterType.Regex, regex));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"Invalid filter pattern: {pattern}", ex);
            }
            catch (RegexMatchTimeoutException ex)
            {
                throw new InvalidOperationException($"Filter pattern timed out during compilation: {pattern}", ex);
            }
        }
    }

    private static bool HasConfiguredFilter(Configuration config)
    {
        if (!string.IsNullOrWhiteSpace(config.FilterFile)) return true;

        foreach (var pattern in config.FilterPatterns)
            if (!string.IsNullOrWhiteSpace(pattern))
                return true;

        return false;
    }

    /// <summary>
    /// Checks if a URL is allowed based on filter rules.
    /// Aligns with tinyproxy C's filter_run function.
    /// </summary>
    public bool IsAllowed(string url)
    {
        // When default deny is enabled, empty rule set denies by default.
        if (_regexRules.Count == 0 && _globRules.Count == 0) return !_config.FilterDefaultDeny;

        var matched = false;

        // Check regex rules with timeout protection
        foreach (var rule in _regexRules)
            if (rule.Regex?.IsMatch(url) == true)
            {
                matched = true;
                break;
            }

        // Check glob rules
        if (!matched)
            foreach (var rule in _globRules)
                if (MatchGlob(url, rule.Pattern))
                {
                    matched = true;
                    break;
                }

        // Determine final result based on FilterDefaultDeny
        if (_config.FilterDefaultDeny)
            // Default deny, allow only if explicitly matched
            return matched;
        else
            // Default allow, deny if matched
            return !matched;
    }

    /// <summary>
    /// Checks if a request is allowed based on its URI.
    /// </summary>
    public bool IsRequestAllowed(Protocol.Http.HttpRequest request)
    {
        var target = GetFilterTarget(request);
        return IsAllowed(target);
    }

    /// <summary>
    /// Matches a string against a glob pattern.
    /// Supports * (matches any sequence) and ? (matches single character).
    /// </summary>
    private static bool MatchGlob(string input, string pattern)
    {
        var inputIndex = 0;
        var patternIndex = 0;
        var starIndex = -1;
        var inputBacktrackIndex = -1;

        while (inputIndex < input.Length)
        {
            if (patternIndex < pattern.Length)
            {
                var patternChar = pattern[patternIndex];

                if (patternChar == '?')
                {
                    // ? matches any single character
                    inputIndex++;
                    patternIndex++;
                    continue;
                }
                else if (patternChar == '*')
                {
                    // * matches any sequence
                    starIndex = patternIndex;
                    inputBacktrackIndex = inputIndex;
                    patternIndex++;
                    continue;
                }
                else if (input[inputIndex] == patternChar)
                {
                    // Exact match
                    inputIndex++;
                    patternIndex++;
                    continue;
                }
            }

            // If we have a * to backtrack to
            if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                inputBacktrackIndex++;
                inputIndex = inputBacktrackIndex;
                continue;
            }

            return false;
        }

        // Skip trailing * in pattern
        while (patternIndex < pattern.Length && pattern[patternIndex] == '*') patternIndex++;

        return patternIndex == pattern.Length;
    }

    private string GetFilterTarget(Protocol.Http.HttpRequest request)
    {
        if (_config.FilterUrls)
            return GetUrlFilterTarget(request);

        // tinyproxy default: filter by domain/host
        if (request.Method == Protocol.Http.HttpMethod.Connect &&
            TextUtils.TryParseHostPort(request.Uri, 443, out var connectHost, out _))
            return connectHost;

        if (request.TryGetTarget(out var host, out _))
            return host;

        if (!string.IsNullOrWhiteSpace(request.Host) &&
            TextUtils.TryParseHostPort(request.Host, 80, out var hostHeader, out _))
            return hostHeader;

        return request.Host ?? request.Uri;
    }

    private static string GetUrlFilterTarget(Protocol.Http.HttpRequest request)
    {
        // tinyproxy C filters CONNECT by the original URL token (host:port),
        // not by a synthesized absolute URI.
        if (request.Method == Protocol.Http.HttpMethod.Connect)
            return request.Uri;

        if (request.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            request.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return request.Uri;

        if (!string.IsNullOrWhiteSpace(request.Host))
            return $"http://{request.Host}{request.Uri}";

        return request.Uri;
    }
}

/// <summary>
/// Represents a single filter rule.
/// </summary>
internal sealed class FilterRule
{
    public string Pattern { get; }
    public FilterType Type { get; }
    public Regex? Regex { get; }

    public FilterRule(string pattern, FilterType type, Regex? regex = null)
    {
        Pattern = pattern;
        Type = type;
        Regex = regex;
    }
}

/// <summary>
/// Type of filter (regex or glob).
/// </summary>
internal enum FilterType
{
    Regex,
    Glob
}
