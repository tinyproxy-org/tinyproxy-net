using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace TinyProxy.Config;

public sealed partial class ConfigParser
{
    private static readonly Regex s_commentRegex = CommentRegex();
    private static readonly Regex s_directiveRegex = DirectiveRegex();

    /// <summary>
    /// Executes parse.
    /// </summary>
    public static Configuration Parse(string content)
    {
        var config = new Configuration();
        var allowIPs = new HashSet<string>();
        var denyIPs = new HashSet<string>();
        var accessRules = new List<AclRuleConfig>();
        var filterPatterns = new List<string>();
        var allowedConnectPorts = new HashSet<ushort>();
        var anonymousAllowedHeaders = new HashSet<string>();
        var basicAuthUsers = new List<BasicAuthUser>();
        BasicAuthConfig? primaryBasicAuth = null;
        var reversePaths = new List<ReversePathConfig>();
        var customErrorPages = new Dictionary<int, string>();
        var customHeaders = new List<HttpHeader>();
        var upstreamRules = new List<UpstreamProxyRuleConfig>();

        var lines = content.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index];
            var trimmed = line.Trim();

            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#')) continue;

            var match = s_directiveRegex.Match(trimmed);
            if (!match.Success)
                throw new FormatException($"Unable to parse configuration line {lineNumber}: '{trimmed}'");

            var directive = match.Groups[1].Value;
            var value = NormalizeDirectiveValue(match.Groups[2].Value);

            switch (directive.ToLowerInvariant())
            {
                case "listen":
                    var address = ParseListenAddress(value, lineNumber);
                    config = config with
                    {
                        ListenAddress = address
                    };
                    break;

                case "port":
                    config = config with { ListenPort = ParsePortDirectiveValue("Port", value, lineNumber) };
                    break;

                case "maxclients":
                    config = config with { MaxClients = ParseUnsignedIntDirectiveValue("MaxClients", value, lineNumber) };
                    break;

                case "maxclientsperip":
                    config = config with { MaxClientsPerIp = ParseUnsignedIntDirectiveValue("MaxClientsPerIp", value, lineNumber) };
                    break;

                case "timeout":
                    var timeoutSeconds = ParseUnsignedIntDirectiveValue("Timeout", value, lineNumber);
                    if (timeoutSeconds == 0)
                        timeoutSeconds = ProxyConstants.DefaultConnectionTimeoutSeconds;
                    config = config with { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
                    break;

                case "connecttimeout":
                    config = config with { ConnectIdleTimeout = TimeSpan.FromSeconds(ParseUnsignedIntDirectiveValue("ConnectTimeout", value, lineNumber)) };
                    break;

                case "allow":
                    allowIPs.Add(value);
                    accessRules.Add(new AclRuleConfig { IsAllow = true, Pattern = value });
                    break;

                case "deny":
                    denyIPs.Add(value);
                    accessRules.Add(new AclRuleConfig { IsAllow = false, Pattern = value });
                    break;

                case "filterurl":
                    // "FilterURL" accepts either a boolean toggle or an inline pattern.
                    if (TryParseTinyProxyBoolean(value, out var filterUrlsLegacy))
                        config = config with { FilterUrls = filterUrlsLegacy };
                    else
                        filterPatterns.Add(value);
                    break;

                case "filterurls":
                    config = config with { FilterUrls = ParseBooleanDirectiveValue("FilterURLs", value, lineNumber) };
                    break;

                case "filter":
                    // "Filter" is treated as a file path and loaded into FilterPatterns.
                    config = config with { FilterFile = value };
                    filterPatterns.AddRange(LoadFilterFile(value));
                    break;

                case "filterdefaultdeny":
                    config = config with { FilterDefaultDeny = ParseBooleanDirectiveValue("FilterDefaultDeny", value, lineNumber) };
                    break;

                case "filtercasesensitive":
                    config = config with { FilterCaseSensitive = ParseBooleanDirectiveValue("FilterCaseSensitive", value, lineNumber) };
                    break;

                case "filtertype":
                    if (value.Equals("fnmatch", StringComparison.OrdinalIgnoreCase))
                        config = config with { FilterUseGlob = true };
                    else if (value.Equals("bre", StringComparison.OrdinalIgnoreCase) ||
                             value.Equals("ere", StringComparison.OrdinalIgnoreCase))
                        config = config with { FilterUseGlob = false };
                    else
                        throw new FormatException($"Invalid value '{value}' for FilterType at line {lineNumber}");
                    break;

                case "connectport":
                    allowedConnectPorts.Add(ParsePortDirectiveValue("ConnectPort", value, lineNumber));
                    break;

                case "logfile":
                    config = config with { LogFile = value };
                    break;

                case "syslog":
                    config = config with { UseSyslog = ParseBooleanDirectiveValue("Syslog", value, lineNumber) };
                    break;

                case "syslogserver":
                    config = config with { SyslogServer = value };
                    break;

                case "syslogport":
                    config = config with { SyslogPort = ParsePortNumberDirectiveValue("SyslogPort", value, lineNumber) };
                    break;

                case "viaheader":
                    config = config with { AddViaHeader = ParseBooleanDirectiveValue("ViaHeader", value, lineNumber) };
                    break;

                case "disableviaheader":
                    config = config with { AddViaHeader = !ParseBooleanDirectiveValue("DisableViaHeader", value, lineNumber) };
                    break;

                case "viaproxyname":
                    config = config with { ViaProxyName = value };
                    break;

                case "xtinyproxy":
                    config = config with { AddXTinyproxyHeader = ParseBooleanDirectiveValue("XTinyproxy", value, lineNumber) };
                    break;

                case "verbose":
                    config = config with { Verbose = ParseBooleanDirectiveValue("Verbose", value, lineNumber) };
                    break;

                case "anonymous":
                    anonymousAllowedHeaders.Add(value);
                    break;

                case "stathost":
                    config = config with { StatHost = value };
                    break;

                case "pidfile":
                    config = config with { PidFile = value };
                    break;

                case "bindsame":
                    config = config with { BindSame = ParseBooleanDirectiveValue("BindSame", value, lineNumber) };
                    break;

                case "reverseproxy":
                case "reversepath":
                    if (TryParseReversePath(value, out var reversePath))
                        config = AddReversePath(config, reversePaths, reversePath);
                    break;

                case "reversemagic":
                    config = config with { ReverseMagicEnabled = ParseBooleanDirectiveValue("ReverseMagic", value, lineNumber) };
                    break;

                case "reverseonly":
                    config = config with { ReverseOnly = ParseBooleanDirectiveValue("ReverseOnly", value, lineNumber) };
                    break;

                case "reversebaseurl":
                    config = config with { ReverseBaseUrl = value };
                    break;

                case "transparent":
                    config = config with { IsTransparentProxyEnabled = ParseBooleanDirectiveValue("Transparent", value, lineNumber) };
                    break;

                case "basicauth":
                    ParseBasicAuth(value, out var baUser, out var baPass);
                    if (baUser != null && baPass != null)
                    {
                        basicAuthUsers.Add(new BasicAuthUser { Username = baUser, Password = baPass });
                        if (primaryBasicAuth == null)
                            primaryBasicAuth = new BasicAuthConfig { Username = baUser, Password = baPass };
                    }

                    break;

                case "upstream":
                    if (TryParseUpstream(value, out var upstreamRule))
                        config = AddUpstreamRule(config, upstreamRules, upstreamRule);
                    break;

                case "errorfile":
                    // Format: ErrorFile <status-code> <file-path>
                    var parts = value.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && int.TryParse(parts[0], out var code))
                    {
                        customErrorPages[code] = parts[1];
                        config = config with { CustomErrorPages = new Dictionary<int, string>(customErrorPages) };
                    }

                    break;

                case "errorpagesdirectory":
                    config = config with { ErrorPagesDirectory = value };
                    break;

                case "addheader":
                    // "AddHeader" supports both two-token form and "Name: Value" form.
                    if (TryParseAddHeader(value, out var customHeader))
                    {
                        customHeaders.Add(customHeader);
                        config = config with { CustomHeaders = new List<HttpHeader>(customHeaders) };
                    }

                    break;

                default:
                    throw new FormatException($"Unknown directive '{directive}' at line {lineNumber}");
            }
        }

        config = config with
        {
            AllowIPs = allowIPs,
            DenyIPs = denyIPs,
            AccessRules = accessRules,
            FilterPatterns = filterPatterns,
            AllowedConnectPorts = allowedConnectPorts,
            AnonymousAllowedHeaders = anonymousAllowedHeaders,
            BasicAuth = primaryBasicAuth,
            BasicAuthUsers = basicAuthUsers,
            ReversePaths = reversePaths,
            CustomErrorPages = customErrorPages,
            CustomHeaders = customHeaders,
            UpstreamProxyRules = upstreamRules
        };

        ValidateFilterPatterns(config);

        return config;
    }

    private static string ParseListenAddress(string value, int lineNumber)
    {
        var address = value.Trim();
        if (address.Length == 0 || !IPAddress.TryParse(address, out _))
            throw new FormatException($"Invalid value '{value}' for Listen at line {lineNumber}");

        return address;
    }

    private static ushort ParsePortDirectiveValue(string directive, string value, int lineNumber)
    {
        if (!ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            throw new FormatException($"Invalid value '{value}' for {directive} at line {lineNumber}");

        return parsed;
    }

    private static int ParsePortNumberDirectiveValue(string directive, string value, int lineNumber)
    {
        var parsed = ParseUnsignedIntDirectiveValue(directive, value, lineNumber);
        if (parsed > ushort.MaxValue)
            throw new FormatException($"Invalid value '{value}' for {directive} at line {lineNumber}");

        return parsed;
    }

    private static int ParseUnsignedIntDirectiveValue(string directive, string value, int lineNumber)
    {
        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed > int.MaxValue)
            throw new FormatException($"Invalid value '{value}' for {directive} at line {lineNumber}");

        return (int)parsed;
    }

    private static bool ParseBooleanDirectiveValue(string directive, string value, int lineNumber)
    {
        if (!TryParseTinyProxyBoolean(value, out var parsed))
            throw new FormatException($"Invalid value '{value}' for {directive} at line {lineNumber}");

        return parsed;
    }

    private static Configuration AddReversePath(
        Configuration config,
        List<ReversePathConfig> reversePaths,
        ReversePathConfig reversePath)
    {
        reversePaths.Insert(0, reversePath);
        return config with { IsReverseProxyEnabled = true };
    }

    private static bool TryParseReversePath(string value, out ReversePathConfig reversePath)
    {
        reversePath = default!;

        var tokens = TokenizeArguments(value);
        if (tokens.Count == 0 || tokens.Count > 2) return false;

        var path = tokens.Count == 1 ? "/" : tokens[0];
        var url = tokens.Count == 1 ? tokens[0] : tokens[1];

        if (string.IsNullOrWhiteSpace(path) ||
            string.IsNullOrWhiteSpace(url) ||
            !url.Contains("://", StringComparison.Ordinal))
            return false;

        if (!TryNormalizeReversePath(path, out var normalizedPath)) return false;

        reversePath = new ReversePathConfig { Path = normalizedPath, Url = url };
        return true;
    }

    private static bool TryNormalizeReversePath(string path, out string normalizedPath)
    {
        normalizedPath = string.Empty;

        var trimmed = path.Trim();
        if (!trimmed.StartsWith('/')) return false;

        normalizedPath = trimmed.EndsWith('/') ? trimmed : $"{trimmed}/";
        return true;
    }

    private static void ParseBasicAuth(string value, out string? username, out string? password)
    {
        username = null;
        password = null;

        var tokens = TokenizeArguments(value);

        if (tokens.Count >= 2)
        {
            username = tokens[0];
            password = tokens[1];
            return;
        }

        if (tokens.Count == 1)
        {
            var colonIndex = tokens[0].IndexOf(':');
            if (colonIndex <= 0) return;

            username = tokens[0].Substring(0, colonIndex);
            password = tokens[0].Substring(colonIndex + 1);
        }
    }

    private static Configuration AddUpstreamRule(
        Configuration config,
        List<UpstreamProxyRuleConfig> rules,
        UpstreamProxyRuleConfig rule)
    {
        if (rule.Domain == null)
        {
            if (rules.Any(r => r.Domain == null)) return config;

            rules.Add(rule);

            if (rule.Proxy != null)
                return config with { UpstreamProxy = rule.Proxy };

            return config;
        }


        rules.Insert(0, rule);
        return config;
    }

    private static bool TryParseUpstream(string value, out UpstreamProxyRuleConfig rule)
    {
        rule = new UpstreamProxyRuleConfig();

        var tokens = TokenizeArguments(value);
        if (tokens.Count == 0) return false;

        // "Upstream none <domain>" creates a domain rule that bypasses upstream proxying.
        if (tokens[0].Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            if (tokens.Count < 2 || string.IsNullOrWhiteSpace(tokens[1])) return false;
            rule = new UpstreamProxyRuleConfig { Domain = tokens[1], Proxy = null };
            return true;
        }

        var type = UpstreamProxyType.Http;
        var index = 0;

        if (tokens[index].Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            type = UpstreamProxyType.Http;
            index++;
        }
        else if (tokens[index].Equals("socks4", StringComparison.OrdinalIgnoreCase))
        {
            type = UpstreamProxyType.Socks4;
            index++;
        }
        else if (tokens[index].Equals("socks5", StringComparison.OrdinalIgnoreCase))
        {
            type = UpstreamProxyType.Socks5;
            index++;
        }

        if (index >= tokens.Count) return false;

        var endpointToken = tokens[index++];
        string? domain = null;
        if (index < tokens.Count && !string.IsNullOrWhiteSpace(tokens[index]))
            domain = tokens[index];

        if (TryStripScheme(endpointToken, out var schemeType, out var strippedEndpoint))
        {
            type = schemeType;
            endpointToken = strippedEndpoint;
        }

        if (!TryParseUpstreamEndpoint(endpointToken, out var host, out var port, out var username, out var password))
            return false;

        rule = new UpstreamProxyRuleConfig
        {
            Domain = domain,
            Proxy = new UpstreamProxyConfig
            {
                Host = host,
                Port = port,
                Type = type,
                Username = username,
                Password = password,
                Domain = domain
            }
        };

        return true;
    }

    private static bool TryStripScheme(string endpointToken, out UpstreamProxyType type, out string stripped)
    {
        type = UpstreamProxyType.Http;
        stripped = endpointToken;

        if (endpointToken.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            type = UpstreamProxyType.Http;
            stripped = endpointToken.Substring(7);
            return true;
        }

        if (endpointToken.StartsWith("socks4://", StringComparison.OrdinalIgnoreCase))
        {
            type = UpstreamProxyType.Socks4;
            stripped = endpointToken.Substring(9);
            return true;
        }

        if (endpointToken.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
        {
            type = UpstreamProxyType.Socks5;
            stripped = endpointToken.Substring(9);
            return true;
        }

        return false;
    }

    private static bool TryParseUpstreamEndpoint(
        string endpointToken,
        out string host,
        out ushort port,
        out string? username,
        out string? password)
    {
        host = string.Empty;
        port = 0;
        username = null;
        password = null;

        if (string.IsNullOrWhiteSpace(endpointToken)) return false;

        var hostPortToken = endpointToken;
        var atIndex = endpointToken.LastIndexOf('@');
        if (atIndex > 0)
        {
            var credentials = endpointToken.Substring(0, atIndex);
            hostPortToken = endpointToken.Substring(atIndex + 1);

            var colonInCredentials = credentials.IndexOf(':');
            if (colonInCredentials <= 0 || colonInCredentials >= credentials.Length - 1) return false;
            username = credentials.Substring(0, colonInCredentials);
            password = credentials.Substring(colonInCredentials + 1);
        }

        if (!TryParseHostAndPort(hostPortToken, out host, out port))
            return false;

        return !string.IsNullOrEmpty(host) && port > 0;
    }

    private static bool TryParseHostAndPort(string input, out string host, out ushort port)
    {
        host = string.Empty;
        port = 0;

        if (string.IsNullOrWhiteSpace(input)) return false;

        var span = input.AsSpan().Trim();

        if (span.Length > 0 && span[0] == '[')
        {
            var closeBracketIndex = span.IndexOf(']');
            if (closeBracketIndex <= 1 || closeBracketIndex >= span.Length - 1) return false;
            if (span[closeBracketIndex + 1] != ':') return false;

            host = span[1..closeBracketIndex].ToString();
            return ushort.TryParse(span[(closeBracketIndex + 2)..], out port) && !string.IsNullOrWhiteSpace(host);
        }

        var colonIndex = span.LastIndexOf(':');
        if (colonIndex <= 0 || colonIndex >= span.Length - 1) return false;

        host = span[..colonIndex].ToString();
        return ushort.TryParse(span[(colonIndex + 1)..], out port) && !string.IsNullOrWhiteSpace(host);
    }

    /// <summary>
    /// Loads from file.
    /// </summary>
    public static Configuration LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Could not open config file \"{Path.GetFullPath(path)}\".\n" +
                "Usage: tinyproxy [-c <config-file>]\n" +
                "Default config locations: /etc/tinyproxy/tinyproxy.conf or ./tinyproxy.conf",
                path);
        var content = File.ReadAllText(path);
        return Parse(content);
    }

    /// <summary>
    /// Supports yes/no, on/off, true/false, 1/0.
    /// </summary>
    private static bool TryParseTinyProxyBoolean(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "yes":
            case "on":
            case "true":
            case "1":
                result = true;
                return true;
            case "no":
            case "off":
            case "false":
            case "0":
                result = false;
                return true;
            default:
                return bool.TryParse(value, out result);
        }
    }

    private static string NormalizeDirectiveValue(string rawValue)
    {
        var trimmed = rawValue.Trim();

        if (trimmed.Length < 2 || trimmed[0] != '"' || trimmed[^1] != '"')
            return trimmed;

        var hasNestedQuotes = trimmed[1..^1].IndexOf('"') >= 0;
        return hasNestedQuotes ? trimmed : trimmed[1..^1];
    }

    /// <summary>
    /// Loads filter patterns from a file.
    /// </summary>
    public static List<string> LoadFilterFile(string path)
    {
        var patterns = new List<string>();

        try
        {
            foreach (var line in File.ReadAllLines(path))
                if (TryExtractFilterPattern(line.AsSpan(), out var pattern))
                    patterns.Add(pattern);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load filter file '{path}': {ex.Message}", ex);
        }

        return patterns;
    }

    private static bool TryExtractFilterPattern(ReadOnlySpan<char> line, out string pattern)
    {
        pattern = string.Empty;

        var index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index])) index++;
        if (index >= line.Length) return false;

        var start = index;
        while (index < line.Length)
        {
            var current = line[index];
            if (char.IsWhiteSpace(current)) break;

            if (current == '#' && (index == 0 || line[index - 1] != '\\'))
                break;

            index++;
        }

        if (index <= start) return false;

        pattern = line[start..index].ToString();
        return !string.IsNullOrWhiteSpace(pattern);
    }

    private static void ValidateFilterPatterns(Configuration config)
    {
        if (config.FilterUseGlob) return;

        var options = config.FilterCaseSensitive
            ? RegexOptions.None
            : RegexOptions.IgnoreCase;

        for (var index = 0; index < config.FilterPatterns.Count; index++)
        {
            var pattern = config.FilterPatterns[index];
            if (string.IsNullOrWhiteSpace(pattern)) continue;

            try
            {
                _ = new Regex(pattern, options);
            }
            catch (ArgumentException ex)
            {
                throw new FormatException(
                    $"Invalid filter regex at pattern #{index + 1}: '{pattern}'",
                    ex);
            }
        }
    }

    private static List<string> TokenizeArguments(string value)
    {
        var result = new List<string>();
        var span = value.AsSpan();
        var index = 0;

        while (index < span.Length)
        {
            while (index < span.Length && char.IsWhiteSpace(span[index])) index++;
            if (index >= span.Length) break;

            if (span[index] == '"')
            {
                index++;
                var start = index;
                while (index < span.Length && span[index] != '"') index++;
                result.Add(span[start..index].ToString().Trim('"'));
                if (index < span.Length && span[index] == '"') index++;
                continue;
            }

            var tokenStart = index;
            while (index < span.Length && !char.IsWhiteSpace(span[index])) index++;
            result.Add(span[tokenStart..index].ToString().Trim('"'));
        }

        return result;
    }

    private static bool TryParseAddHeader(string value, out HttpHeader header)
    {
        header = default!;

        var trimmed = value.Trim();
        var tokens = TokenizeArguments(trimmed);
        if (tokens.Count >= 2 &&
            !tokens[0].Contains(':', StringComparison.Ordinal) &&
            !string.Equals(tokens[1], ":", StringComparison.Ordinal))
        {
            var headerName = tokens[0].Trim();
            var headerValue = string.Join(' ', tokens.Skip(1)).Trim();

            if (headerName.Length == 0 || headerValue.Length == 0) return false;

            header = new HttpHeader
            {
                Name = headerName,
                Value = headerValue
            };
            return true;
        }

        var colonIndex = trimmed.IndexOf(':');
        if (colonIndex <= 0 || colonIndex >= trimmed.Length - 1) return false;

        var legacyName = trimmed.Substring(0, colonIndex).Trim();
        var legacyValue = trimmed.Substring(colonIndex + 1).Trim();
        if (legacyName.Length == 0 || legacyValue.Length == 0) return false;

        header = new HttpHeader
        {
            Name = legacyName,
            Value = legacyValue
        };
        return true;
    }

    [GeneratedRegex(@"^\s*#.*$")]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"^\s*(\w+)\s+(.+)$")]
    private static partial Regex DirectiveRegex();
}