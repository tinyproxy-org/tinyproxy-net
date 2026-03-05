namespace TinyProxy.Tests.Filter;

public class AccessControlTests
{
    [Fact]
    public void IsAllowed_WithDenyRulesOnly_DefaultsToDenyWhenNoRuleMatches()
    {
        var config = new Configuration
        {
            DenyIPs = new HashSet<string> { "10.0.0.1" }
        };

        var accessControl = new AccessControl(config);

        Assert.False(accessControl.IsAllowed("10.0.0.2"));
    }

    [Theory]
    [InlineData("10.0.0.0/33", "10.0.0.1")]
    [InlineData("2001:db8::/129", "2001:db8::1")]
    [InlineData("10.0.0.0/-1", "10.0.0.1")]
    public void IsAllowed_WithInvalidCidrPrefix_DoesNotThrowAndFallsBackToDeny(string cidrRule, string clientIp)
    {
        var config = new Configuration
        {
            DenyIPs = new HashSet<string> { cidrRule }
        };

        var accessControl = new AccessControl(config);

        var ex = Record.Exception(() => accessControl.IsAllowed(clientIp));
        Assert.Null(ex);
        Assert.False(accessControl.IsAllowed(clientIp));
    }

    [Theory]
    [InlineData("10.0.0.0/not-a-prefix", "10.0.0.1")]
    [InlineData("bad/ip", "10.0.0.1")]
    public void IsAllowed_WithInvalidSlashRule_IgnoresRuleAndFallsBackToDeny(string invalidRule, string clientIp)
    {
        var config = new Configuration
        {
            AccessRules = new List<AclRuleConfig>
            {
                new() { IsAllow = false, Pattern = invalidRule }
            }
        };

        var accessControl = new AccessControl(config);
        Assert.False(accessControl.IsAllowed(clientIp));
    }

    [Fact]
    public void IsAllowed_WithNoAclDirectives_FallsBackToAllow()
    {
        var accessControl = new AccessControl(Configuration.Default with { Verbose = false });
        Assert.True(accessControl.IsAllowed("10.0.0.1"));
    }

    [Fact]
    public void IsAllowed_Ipv4Rule_MatchesIpv4MappedIpv6ClientAddress()
    {
        var config = new Configuration
        {
            AccessRules = new List<AclRuleConfig>
            {
                new() { IsAllow = true, Pattern = "127.0.0.1" }
            }
        };

        var accessControl = new AccessControl(config);
        Assert.True(accessControl.IsAllowed("::ffff:127.0.0.1"));
    }

    [Fact]
    public void IsAllowed_WhenAclRulesOrdered_FirstMatchWins()
    {
        var allowFirst = new Configuration
        {
            AccessRules = new List<AclRuleConfig>
            {
                new() { IsAllow = true, Pattern = "127.0.0.1" },
                new() { IsAllow = false, Pattern = "127.0.0.1" }
            }
        };

        var denyFirst = new Configuration
        {
            AccessRules = new List<AclRuleConfig>
            {
                new() { IsAllow = false, Pattern = "127.0.0.1" },
                new() { IsAllow = true, Pattern = "127.0.0.1" }
            }
        };

        Assert.True(new AccessControl(allowFirst).IsAllowed("127.0.0.1"));
        Assert.False(new AccessControl(denyFirst).IsAllowed("127.0.0.1"));
    }

    [Fact]
    public void IsAllowed_DotPrefixedDomainRule_DoesNotMatchBareDomain()
    {
        var config = new Configuration
        {
            AccessRules = new List<AclRuleConfig>
            {
                new() { IsAllow = true, Pattern = ".example.com" }
            }
        };

        var accessControl = new AccessControl(config);
        Assert.False(accessControl.IsAllowed("example.com"));
        Assert.True(accessControl.IsAllowed("www.example.com"));
    }

    [Fact]
    public async Task ProcessAsync_UsesDnsAwareAclPath_ForDomainAllowRule()
    {
        var hostEntry = await Dns.GetHostEntryAsync(IPAddress.Loopback.ToString());
        Assert.False(string.IsNullOrWhiteSpace(hostEntry.HostName));

        var config = Configuration.Default with
        {
            Verbose = false,
            AllowIPs = new HashSet<string> { hostEntry.HostName }
        };

        var response = await SendRequestAsync(config, "INVALID_REQUEST_LINE\r\n\r\n");

        Assert.Contains("400 Bad Request", response, StringComparison.Ordinal);
        Assert.DoesNotContain("403 Forbidden", response, StringComparison.Ordinal);
    }

    private static async Task<string> SendRequestAsync(Configuration config, string rawRequest)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);
        using var server = await listener.AcceptAsync();
        await connectTask;

        var logger = new TestLogger();
        var stats = new Stats();
        using var accessLogger = new AccessLogger(config, logger);
        var loopDetector = new LoopDetector();
        using var connection = new Connection(server, logger, config, stats, accessLogger, loopDetector);

        var requestBytes = Encoding.ASCII.GetBytes(rawRequest);
        await client.SendAsync(requestBytes, SocketFlags.None);
        client.Shutdown(SocketShutdown.Send);

        await connection.ProcessAsync();
        connection.Dispose();

        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (true)
        {
            int read;
            try
            {
                read = await client.ReceiveAsync(buffer, SocketFlags.None, cts.Token);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.OperationAborted)
            {
                break;
            }

            if (read <= 0) break;
            ms.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private sealed class TestLogger : ILogger
    {
        public void LogInfo(string message) { }
        public void LogError(string message) { }
        public void LogWarning(string message) { }
        public void LogConnect(string message) { }
        public void LogCritical(string message) { }
    }
}
