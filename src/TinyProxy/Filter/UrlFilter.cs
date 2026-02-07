using System.Text.RegularExpressions;
using TinyProxy.Config;
using TinyProxy.Core;
using TinyProxy.Logging;

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

    public UrlFilter(Configuration config, ILogger? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? new Core.ConsoleLogger();

        // Parse filter regexes into rules
        _regexRules = new List<FilterRule>();
        _globRules = new List<FilterRule>();

        foreach (var pattern in config.FilterPatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;

            // Check if it's a glob pattern (contains * or ?)
            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                _globRules.Add(new FilterRule(pattern, FilterType.Glob));
            }
            else
            {
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
                    // Invalid regex, log but don't crash
                    _logger.LogWarning($"Invalid filter pattern: {pattern}, error: {ex.Message}");
                }
                catch (RegexMatchTimeoutException ex)
                {
                    // Regex too complex, skip this pattern
                    _logger.LogWarning($"Filter pattern too complex (ReDoS risk): {pattern}, error: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Checks if a URL is allowed based on filter rules.
    /// Aligns with tinyproxy C's filter_run function.
    /// </summary>
    public bool IsAllowed(string url)
    {
        // If no filters configured, allow all
        if (_regexRules.Count == 0 && _globRules.Count == 0)
        {
            return true;
        }

        var matched = false;

        // Check regex rules with timeout protection
        foreach (var rule in _regexRules)
        {
            if (rule.Regex?.IsMatch(url) == true)
            {
                matched = true;
                break;
            }
        }

        // Check glob rules
        if (!matched)
        {
            foreach (var rule in _globRules)
            {
                if (MatchGlob(url, rule.Pattern))
                {
                    matched = true;
                    break;
                }
            }
        }

        // Determine final result based on FilterDefaultDeny
        if (_config.FilterDefaultDeny)
        {
            // Default deny, allow only if explicitly matched
            return matched;
        }
        else
        {
            // Default allow, deny if matched
            return !matched;
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

        // Check path only
        var pathAllowed = IsAllowed(request.Uri);

        // If path is denied, no need to check further
        if (!_config.FilterDefaultDeny && !pathAllowed)
        {
            return false;
        }

        // If default deny and path is allowed, return true
        if (_config.FilterDefaultDeny && pathAllowed)
        {
            return true;
        }

        // Check full URL if host is available
        if (!string.IsNullOrEmpty(request.Host))
        {
            var fullUrl = $"http://{request.Host}{request.Uri}";
            return IsAllowed(fullUrl);
        }

        return pathAllowed;
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
        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
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
