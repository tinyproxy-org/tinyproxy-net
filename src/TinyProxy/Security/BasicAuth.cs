using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using TinyProxy.Config;

namespace TinyProxy.Security;

/// <summary>
/// HTTP Basic Authentication validator.
/// Aligns with tinyproxy C's basicauth.c implementation.
/// Supports multiple users with constant-time comparison for security.
/// </summary>
public sealed class BasicAuth
{
    private readonly Configuration _config;
    private readonly Dictionary<string, string> _credentials;

    public BasicAuth(Configuration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _credentials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        
        // Initialize credentials from config
        if (_config.BasicAuth != null)
        {
            _credentials[_config.BasicAuth.Username] = _config.BasicAuth.Password;
        }
        
        // Add any additional credentials
        if (_config.BasicAuthUsers != null)
        {
            foreach (var user in _config.BasicAuthUsers)
            {
                _credentials[user.Username] = user.Password;
            }
        }
    }

    /// <summary>
    /// Validates the Authorization header against configured credentials.
    /// Uses constant-time comparison to prevent timing attacks.
    /// Aligns with tinyproxy C's basicauth_check function.
    /// </summary>
    public bool Validate(string? authorizationHeader)
    {
        // No auth configured, allow all
        if (_credentials.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return false;
        }

        // Expected format: "Basic base64(username:password)"
        if (!authorizationHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var base64Part = authorizationHeader.Substring(6).Trim();

        byte[] decodedBytes;
        try
        {
            decodedBytes = Convert.FromBase64String(base64Part);
        }
        catch (FormatException)
        {
            return false;
        }

        var decoded = Encoding.UTF8.GetString(decodedBytes);

        // Parse username:password
        var colonIndex = decoded.IndexOf(':');
        if (colonIndex < 0)
        {
            return false;
        }

        var username = decoded.Substring(0, colonIndex);
        var password = decoded.Substring(colonIndex + 1);

        // Look up user credentials
        if (!_credentials.TryGetValue(username, out var storedPassword))
        {
            return false;
        }

        // Use constant-time comparison to prevent timing attacks
        return ConstantTimeEquals(password, storedPassword);
    }

    /// <summary>
    /// Validates username and password directly.
    /// Useful for non-header-based validation.
    /// Aligns with tinyproxy C's basicauth_check function (internal variant).
    /// </summary>
    public bool ValidateCredentials(string username, string password)
    {
        if (_credentials.Count == 0)
        {
            return true;
        }

        if (!_credentials.TryGetValue(username, out var storedPassword))
        {
            return false;
        }

        return ConstantTimeEquals(password, storedPassword);
    }

    /// <summary>
    /// Adds a user to the credentials list.
    /// Aligns with tinyproxy C's basicauth_add function.
    /// </summary>
    public void AddUser(string username, string password)
    {
        _credentials[username] = password;
    }

    /// <summary>
    /// Constant-time string comparison to prevent timing attacks.
    /// </summary>
    private static bool ConstantTimeEquals(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);

        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }

    /// <summary>
    /// Extracts the Authorization header from the request headers.
    /// </summary>
    public static string? GetAuthorizationHeader(IDictionary<string, ReadOnlySequence<byte>> headers)
    {
        if (headers.TryGetValue("Proxy-Authorization", out var value) ||
            headers.TryGetValue("Authorization", out value))
        {
            if (value.Length > 0)
            {
                return Encoding.ASCII.GetString(value.ToArray());
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the authentication realm for the WWW-Authenticate header.
    /// </summary>
    public string GetRealm()
    {
        return _config.BasicAuth?.Realm ?? "TinyProxy";
    }

    /// <summary>
    /// Checks if authentication is enabled (has any configured users).
    /// </summary>
    public bool IsEnabled => _credentials.Count > 0;

    /// <summary>
    /// Gets the number of configured users.
    /// </summary>
    public int UserCount => _credentials.Count;
}
