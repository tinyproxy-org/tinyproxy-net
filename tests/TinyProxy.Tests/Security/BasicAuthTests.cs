namespace TinyProxy.Tests.Security;

public class BasicAuthTests
{
    [Fact]
    public void Validate_IsCaseSensitiveForUsername()
    {
        var config = new Configuration
        {
            BasicAuth = new BasicAuthConfig
            {
                Username = "Admin",
                Password = "secret"
            }
        };

        var auth = new BasicAuth(config);

        Assert.True(auth.Validate(BuildBasicHeader("Admin", "secret")));
        Assert.False(auth.Validate(BuildBasicHeader("admin", "secret")));
    }

    [Fact]
    public void Validate_AllowsAnyConfiguredUserFromList()
    {
        var config = new Configuration
        {
            BasicAuthUsers = new List<BasicAuthUser>
            {
                new() { Username = "alice", Password = "a1" },
                new() { Username = "bob", Password = "b2" }
            }
        };

        var auth = new BasicAuth(config);

        Assert.True(auth.Validate(BuildBasicHeader("bob", "b2")));
    }

    [Fact]
    public void Validate_AllowsMultiplePasswordsForSameUsername()
    {
        var config = new Configuration
        {
            BasicAuthUsers = new List<BasicAuthUser>
            {
                new() { Username = "alice", Password = "oldpass" },
                new() { Username = "alice", Password = "newpass" }
            }
        };

        var auth = new BasicAuth(config);

        Assert.True(auth.Validate(BuildBasicHeader("alice", "oldpass")));
        Assert.True(auth.Validate(BuildBasicHeader("alice", "newpass")));
    }

    [Fact]
    public void Validate_AllowsConfiguredEmptyPassword()
    {
        var config = new Configuration
        {
            BasicAuth = new BasicAuthConfig
            {
                Username = "alice",
                Password = string.Empty
            }
        };

        var auth = new BasicAuth(config);

        Assert.True(auth.Validate(BuildBasicHeader("alice", string.Empty)));
        Assert.False(auth.Validate(BuildBasicHeader("alice", "not-empty")));
    }

    private static string BuildBasicHeader(string username, string password)
    {
        var raw = $"{username}:{password}";
        return $"Basic {Convert.ToBase64String(Encoding.ASCII.GetBytes(raw))}";
    }
}
