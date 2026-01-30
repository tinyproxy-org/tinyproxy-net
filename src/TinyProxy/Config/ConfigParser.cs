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
        var filterRegexes = new List<Regex>();
        var allowedConnectPorts = new HashSet<ushort> { 443 };
        var anonymousAllowedHeaders = new HashSet<string>();

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

                case "timeout":
                    if (int.TryParse(value, out var t))
                    {
                        config = config with { Timeout = TimeSpan.FromSeconds(t) };
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
                    try
                    {
                        filterRegexes.Add(new Regex(value, RegexOptions.Compiled | RegexOptions.IgnoreCase));
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"Invalid filter regex '{value}': {ex.Message}", ex);
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

                case "viaheader":
                    if (bool.TryParse(value, out var via))
                    {
                        config = config with { AddViaHeader = via };
                    }
                    break;

                case "verbose":
                    if (bool.TryParse(value, out var verbose))
                    {
                        config = config with { Verbose = verbose };
                    }
                    break;

                // Aligns with tinyproxy C's Anonymous directive
                case "anonymous":
                    anonymousAllowedHeaders.Add(value);
                    break;
            }
        }

        // Apply collections
        config = config with
        {
            AllowIPs = allowIPs,
            DenyIPs = denyIPs,
            FilterRegexes = filterRegexes,
            AllowedConnectPorts = allowedConnectPorts,
            AnonymousAllowedHeaders = anonymousAllowedHeaders
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

    public static Configuration LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Configuration file not found: {path}");
        }

        var content = File.ReadAllText(path);
        return Parse(content);
    }

    [GeneratedRegex(@"^\s*([^""\s]+)\s+(.+?)\s*$")]
    private static partial Regex DirectiveRegex();

    [GeneratedRegex(@"#.*$")]
    private static partial Regex CommentRegex();
}
