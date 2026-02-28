using System.Text.RegularExpressions;

namespace TinyProxy.Config;

/// <summary>
/// Parses tinyproxy.conf style configuration files.
/// </summary>
public sealed partial class ConfigParser
{
    private static readonly Regex s_commentRegex = CommentRegex();
    private static readonly Regex s_directiveRegex = DirectiveRegex();

    public static Configuration Parse(string content)
    {
        var config = new Configuration();
        var allowIPs = new HashSet<string>();
        var denyIPs = new HashSet<string>();
        var filterPatterns = new List<string>();
        var allowedConnectPorts = new HashSet<ushort>();
        var anonymousAllowedHeaders = new HashSet<string>();
        var basicAuthUsers = new List<BasicAuthUser>();
        BasicAuthConfig? primaryBasicAuth = null;
        var reversePaths = new List<ReversePathConfig>();
        var customErrorPages = new Dictionary<int, string>();
        var customHeaders = new List<HttpHeader>();

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();

            // Skip empty lines and comments
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#')) continue;

            var match = s_directiveRegex.Match(trimmed);
            if (!match.Success) continue;

            var directive = match.Groups[1].Value;
            var value = match.Groups[2].Value.Trim('"');

            switch (directive.ToLowerInvariant())
            {
                case "listen":
                    ParseListen(value, out var address, out var port);
                    config = config with
                    {
                        ListenAddress = address,
                        ListenPort = port
                    };
                    break;

                case "port":
                    if (ushort.TryParse(value, out var p)) config = config with { ListenPort = p };
                    break;

                case "maxclients":
                    if (int.TryParse(value, out var mc)) config = config with { MaxClients = mc };
                    break;

                case "maxclientsperip":
                    if (int.TryParse(value, out var mcip)) config = config with { MaxClientsPerIp = mcip };
                    break;

                case "timeout":
                    if (int.TryParse(value, out var t)) config = config with { Timeout = TimeSpan.FromSeconds(t) };
                    break;

                case "connecttimeout":
                    if (int.TryParse(value, out var ct)) config = config with { ConnectIdleTimeout = TimeSpan.FromSeconds(ct) };
                    break;

                case "allow":
                    allowIPs.Add(value);
                    break;

                case "deny":
                    denyIPs.Add(value);
                    break;

                case "filterurl":
                    // Compatibility: legacy singular form used as an inline pattern in this project.
                    // tinyproxy C uses "FilterURLs" (plural) as a boolean directive.
                    if (TryParseTinyProxyBoolean(value, out var filterUrlsLegacy))
                        config = config with { FilterUrls = filterUrlsLegacy };
                    else
                        filterPatterns.Add(value);
                    break;

                case "filterurls":
                    if (TryParseTinyProxyBoolean(value, out var filterUrls)) config = config with { FilterUrls = filterUrls };
                    break;

                case "filter":
                    // Can be either inline pattern or file path
                    if (File.Exists(value))
                    {
                        config = config with { FilterFile = value };
                        // Load patterns from file
                        var filePatterns = LoadFilterFile(value);
                        filterPatterns.AddRange(filePatterns);
                    }
                    else
                    {
                        filterPatterns.Add(value);
                    }

                    break;

                case "filterdefaultdeny":
                    if (TryParseTinyProxyBoolean(value, out var fdd)) config = config with { FilterDefaultDeny = fdd };
                    break;

                case "filtercasesensitive":
                    if (TryParseTinyProxyBoolean(value, out var fcs)) config = config with { FilterCaseSensitive = fcs };
                    break;

                case "filtertype":
                    if (value.Equals("fnmatch", StringComparison.OrdinalIgnoreCase))
                        config = config with { FilterUseGlob = true };
                    else if (value.Equals("bre", StringComparison.OrdinalIgnoreCase) ||
                             value.Equals("ere", StringComparison.OrdinalIgnoreCase))
                        config = config with { FilterUseGlob = false };
                    break;

                case "connectport":
                    if (ushort.TryParse(value, out var cp)) allowedConnectPorts.Add(cp);
                    break;

                case "logfile":
                    config = config with { LogFile = value };
                    break;

                case "syslog":
                    if (TryParseTinyProxyBoolean(value, out var syslog)) config = config with { UseSyslog = syslog };
                    break;

                case "syslogserver":
                    config = config with { SyslogServer = value };
                    break;

                case "syslogport":
                    if (int.TryParse(value, out var syslogPort)) config = config with { SyslogPort = syslogPort };
                    break;

                case "viaheader":
                    if (TryParseTinyProxyBoolean(value, out var via)) config = config with { AddViaHeader = via };
                    break;

                case "viaproxyname":
                    config = config with { ViaProxyName = value };
                    break;

                case "xtinyproxy":
                    if (TryParseTinyProxyBoolean(value, out var xtinyproxy)) config = config with { AddXTinyproxyHeader = xtinyproxy };
                    break;

                case "verbose":
                    if (TryParseTinyProxyBoolean(value, out var verbose)) config = config with { Verbose = verbose };
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
                    if (TryParseTinyProxyBoolean(value, out var bs)) config = config with { BindSame = bs };
                    break;

                case "reverseproxy":
                    ParseReverseProxy(value, out var rpPath, out var rpUrl);
                    if (rpPath != null && rpUrl != null)
                    {
                        reversePaths.Add(new ReversePathConfig { Path = rpPath, Url = rpUrl });
                        config = config with { IsReverseProxyEnabled = true };
                    }

                    break;

                case "transparent":
                    if (TryParseTinyProxyBoolean(value, out var tp)) config = config with { IsTransparentProxyEnabled = tp };
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
                    ParseUpstream(value, out var usHost, out var usPort, out var usType);
                    if (usHost != null && usPort > 0) config = config with { UpstreamProxy = new UpstreamProxyConfig { Host = usHost, Port = usPort, Type = usType } };
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
                    // Format: AddHeader "Header-Name: Header-Value"
                    var colonIndex = value.IndexOf(':');
                    if (colonIndex > 0)
                    {
                        var name = value.Substring(0, colonIndex).Trim();
                        var headerValue = value.Substring(colonIndex + 1).Trim();
                        customHeaders.Add(new HttpHeader { Name = name, Value = headerValue });
                        config = config with { CustomHeaders = new List<HttpHeader>(customHeaders) };
                    }

                    break;
            }
        }

        // Apply collections
        config = config with
        {
            AllowIPs = allowIPs,
            DenyIPs = denyIPs,
            FilterPatterns = filterPatterns,
            AllowedConnectPorts = allowedConnectPorts,
            AnonymousAllowedHeaders = anonymousAllowedHeaders,
            BasicAuth = primaryBasicAuth,
            BasicAuthUsers = basicAuthUsers,
            ReversePaths = reversePaths,
            CustomErrorPages = customErrorPages,
            CustomHeaders = customHeaders
        };

        return config;
    }

    private static void ParseListen(string value, out string address, out ushort port)
    {
        address = "127.0.0.1";
        port = 8888;

        var colonIndex = value.LastIndexOf(':');
        if (colonIndex >= 0)
        {
            address = value.Substring(0, colonIndex);
            if (ushort.TryParse(value.Substring(colonIndex + 1), out var p)) port = p;
        }
        else
        {
            address = value;
        }
    }

    private static void ParseReverseProxy(string value, out string? path, out string? url)
    {
        path = null;
        url = null;

        var spaceIndex = value.IndexOf(' ');
        if (spaceIndex > 0)
        {
            path = value.Substring(0, spaceIndex);
            url = value.Substring(spaceIndex + 1).Trim();
        }
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

    private static void ParseUpstream(string value, out string? host, out ushort port, out UpstreamProxyType type)
    {
        host = null;
        port = 0;
        type = UpstreamProxyType.Http;

        // Parse "http://host:port" or "socks5://host:port"
        var url = value.Trim();
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            type = UpstreamProxyType.Http;
            url = url.Substring(7);
        }
        else if (url.StartsWith("socks4://", StringComparison.OrdinalIgnoreCase))
        {
            type = UpstreamProxyType.Socks4;
            url = url.Substring(9);
        }
        else if (url.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
        {
            type = UpstreamProxyType.Socks5;
            url = url.Substring(9);
        }

        var colonIndex = url.LastIndexOf(':');
        if (colonIndex > 0)
        {
            host = url.Substring(0, colonIndex);
            if (ushort.TryParse(url.Substring(colonIndex + 1), out var p)) port = p;
        }
    }

    public static Configuration LoadFromFile(string path)
    {
        var content = File.ReadAllText(path);
        return Parse(content);
    }

    /// <summary>
    /// Parses tinyproxy-style booleans.
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

    /// <summary>
    /// Loads filter patterns from a file.
    /// Aligns with tinyproxy C's filter_init() in filter.c.
    /// </summary>
    public static List<string> LoadFilterFile(string path)
    {
        var patterns = new List<string>();

        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();

                // Skip empty lines and comments
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#')) continue;

                patterns.Add(trimmed);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load filter file '{path}': {ex.Message}", ex);
        }

        return patterns;
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

    [GeneratedRegex(@"^\s*#.*$")]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"^\s*(\w+)\s+(.+)$")]
    private static partial Regex DirectiveRegex();
}
