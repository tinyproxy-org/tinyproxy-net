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
        var allowedConnectPorts = new HashSet<ushort> { 443 };
        var anonymousAllowedHeaders = new HashSet<string>();
        var basicAuthUsers = new List<BasicAuthUser>();
        var reversePaths = new List<ReversePathConfig>();

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();

            // Skip empty lines and comments
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

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
                    if (ushort.TryParse(value, out var p))
                    {
                        config = config with { ListenPort = p };
                    }
                    break;

                case "maxclients":
                    if (int.TryParse(value, out var mc))
                    {
                        config = config with { MaxClients = mc };
                    }
                    break;

                case "maxclientsperip":
                    if (int.TryParse(value, out var mcip))
                    {
                        config = config with { MaxClientsPerIp = mcip };
                    }
                    break;

                case "timeout":
                    if (int.TryParse(value, out var t))
                    {
                        config = config with { Timeout = TimeSpan.FromSeconds(t) };
                    }
                    break;

                case "connecttimeout":
                    if (int.TryParse(value, out var ct))
                    {
                        config = config with { ConnectIdleTimeout = TimeSpan.FromSeconds(ct) };
                    }
                    break;

                case "allow":
                    allowIPs.Add(value);
                    break;

                case "deny":
                    denyIPs.Add(value);
                    break;

                case "filterurl":
                case "filter":
                    filterPatterns.Add(value);
                    break;

                case "filterdefaultdeny":
                    if (bool.TryParse(value, out var fdd))
                    {
                        config = config with { FilterDefaultDeny = fdd };
                    }
                    break;

                case "filtercasesensitive":
                    if (bool.TryParse(value, out var fcs))
                    {
                        config = config with { FilterCaseSensitive = fcs };
                    }
                    break;

                case "connectport":
                    if (ushort.TryParse(value, out var cp))
                    {
                        allowedConnectPorts.Add(cp);
                    }
                    break;

                case "logfile":
                    config = config with { LogFile = value };
                    break;

                case "syslog":
                    if (bool.TryParse(value, out var syslog))
                    {
                        config = config with { UseSyslog = syslog };
                    }
                    break;

                case "viaheader":
                    if (bool.TryParse(value, out var via))
                    {
                        config = config with { AddViaHeader = via };
                    }
                    break;

                case "viaproxyname":
                    config = config with { ViaProxyName = value };
                    break;

                case "xtinyproxy":
                    if (bool.TryParse(value, out var xtinyproxy))
                    {
                        config = config with { AddXTinyproxyHeader = xtinyproxy };
                    }
                    break;

                case "verbose":
                    if (bool.TryParse(value, out var verbose))
                    {
                        config = config with { Verbose = verbose };
                    }
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
                    if (bool.TryParse(value, out var bs))
                    {
                        config = config with { BindSame = bs };
                    }
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
                    if (bool.TryParse(value, out var tp))
                    {
                        config = config with { IsTransparentProxyEnabled = tp };
                    }
                    break;

                case "basicauth":
                    ParseBasicAuth(value, out var baUser, out var baPass);
                    if (baUser != null && baPass != null)
                    {
                        config = config with { BasicAuth = new BasicAuthConfig { Username = baUser, Password = baPass } };
                    }
                    break;

                case "upstream":
                    ParseUpstream(value, out var usHost, out var usPort, out var usType);
                    if (usHost != null && usPort > 0)
                    {
                        config = config with { UpstreamProxy = new UpstreamProxyConfig { Host = usHost, Port = usPort, Type = usType } };
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
            BasicAuthUsers = basicAuthUsers,
            ReversePaths = reversePaths
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
            if (ushort.TryParse(value.Substring(colonIndex + 1), out var p))
            {
                port = p;
            }
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

        var colonIndex = value.IndexOf(':');
        if (colonIndex > 0)
        {
            username = value.Substring(0, colonIndex);
            password = value.Substring(colonIndex + 1);
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
            if (ushort.TryParse(url.Substring(colonIndex + 1), out var p))
            {
                port = p;
            }
        }
    }

    public static Configuration LoadFromFile(string path)
    {
        var content = File.ReadAllText(path);
        return Parse(content);
    }

    [GeneratedRegex(@"^\s*#.*$")]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"^\s*(\w+)\s+(.+)$")]
    private static partial Regex DirectiveRegex();
}
