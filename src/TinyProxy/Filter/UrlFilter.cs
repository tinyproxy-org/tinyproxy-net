using System.Text.RegularExpressions;

namespace TinyProxy.Filter;

/// <summary>
/// URL filtering using regex or glob patterns with ReDoS protection.
/// </summary>
public sealed class UrlFilter
{
    private readonly Configuration _config;
    private readonly ILogger _logger;
    private readonly List<FilterRule> _regexRules;
    private readonly List<FilterRule> _globRules;
    private const int RegexTimeoutMs = 5000; // 5 second timeout for regex matching
    /// <summary>
    /// Gets a value indicating whether enabled.
    /// </summary>
    public bool IsEnabled { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="UrlFilter"/> class.
    /// </summary>
    public UrlFilter(Configuration config, ILogger? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? new ConsoleLogger();
        IsEnabled = HasConfiguredFilter(config);

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
    /// Determines whether allowed.
    /// </summary>
    public bool IsAllowed(string url)
    {
        // When default deny is enabled, empty rule set denies by default.
        if (_regexRules.Count == 0 && _globRules.Count == 0) return !_config.FilterDefaultDeny;

        var matched = false;

        foreach (var rule in _regexRules)
            if (rule.Regex?.IsMatch(url) == true)
            {
                matched = true;
                break;
            }

        if (!matched)
            foreach (var rule in _globRules)
                if (MatchGlob(url, rule.Pattern))
                {
                    matched = true;
                    break;
                }

        return _config.FilterDefaultDeny ? matched : !matched;
    }

    /// <summary>
    /// Determines whether request allowed.
    /// </summary>
    public bool IsRequestAllowed(HttpRequest request)
    {
        var target = GetFilterTarget(request);
        return IsAllowed(target);
    }

    /// <summary>
    /// Matches a string against a glob pattern.
    /// Supports fnmatch-like *, ?, [] classes, [!] negation and backslash escaping.
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
                var escaped = false;

                if (patternChar == '\\' && patternIndex + 1 < pattern.Length)
                {
                    escaped = true;
                    patternChar = pattern[++patternIndex];
                }

                if (!escaped && patternChar == '?')
                {
                    inputIndex++;
                    patternIndex++;
                    continue;
                }
                else if (!escaped && patternChar == '*')
                {
                    starIndex = patternIndex;
                    inputBacktrackIndex = inputIndex;
                    patternIndex++;
                    continue;
                }
                else if (!escaped && patternChar == '[')
                {
                    if (TryParseCharacterClass(pattern, patternIndex, input[inputIndex], out var consumed, out var matchedClass))
                    {
                        if (matchedClass)
                        {
                            inputIndex++;
                            patternIndex += consumed;
                            continue;
                        }
                    }
                    else if (input[inputIndex] == '[')
                    {
                        inputIndex++;
                        patternIndex++;
                        continue;
                    }
                }
                else if (input[inputIndex] == patternChar)
                {
                    inputIndex++;
                    patternIndex++;
                    continue;
                }
            }

            if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                inputBacktrackIndex++;
                inputIndex = inputBacktrackIndex;
                continue;
            }

            return false;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*') patternIndex++;

        return patternIndex == pattern.Length;
    }

    private static bool TryParseCharacterClass(
        string pattern,
        int patternIndex,
        char inputChar,
        out int consumed,
        out bool matched)
    {
        consumed = 1;
        matched = false;

        var index = patternIndex + 1;
        if (index >= pattern.Length) return false;

        var isNegated = false;
        if (pattern[index] is '!' or '^')
        {
            isNegated = true;
            index++;
        }

        if (index >= pattern.Length) return false;

        var hasEntries = false;
        var previousChar = '\0';
        var hasPrevious = false;

        while (index < pattern.Length)
        {
            if (pattern[index] == ']' && hasEntries)
            {
                consumed = index - patternIndex + 1;
                matched = isNegated ? !matched : matched;
                return true;
            }

            var current = pattern[index];
            if (current == '\\' && index + 1 < pattern.Length)
                current = pattern[++index];

            hasEntries = true;

            if (current == '-' && hasPrevious && index + 1 < pattern.Length && pattern[index + 1] != ']')
            {
                index++;
                var rangeEnd = pattern[index];
                if (rangeEnd == '\\' && index + 1 < pattern.Length)
                    rangeEnd = pattern[++index];

                if (inputChar >= previousChar && inputChar <= rangeEnd)
                    matched = true;

                hasPrevious = false;
            }
            else
            {
                if (inputChar == current)
                    matched = true;

                previousChar = current;
                hasPrevious = true;
            }

            index++;
        }

        return false;
    }

    private string GetFilterTarget(HttpRequest request)
    {
        if (_config.FilterUrls)
            return GetUrlFilterTarget(request);

        // When URL filtering is disabled, match on host/domain only.
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

    private static string GetUrlFilterTarget(HttpRequest request)
    {
        // For CONNECT with FilterUrls enabled, match the raw request target (typically host:port).
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