using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using TinyProxy.Config;

namespace TinyProxy.Security;

/// <summary>
/// HTTP Basic Authentication validator.
/// </summary>
public sealed class BasicAuth
{
    private readonly Configuration _config;

    public BasicAuth(Configuration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Validates the Authorization header against configured credentials.
    /// Uses constant-time comparison to prevent timing attacks.
    /// </summary>
    public bool Validate(string? authorizationHeader)
    {
        if (_config.BasicAuth == null)
        {
            // No auth configured, allow all
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

        // Use constant-time comparison to prevent timing attacks
        return ConstantTimeEquals(username, _config.BasicAuth.Username) &&
               ConstantTimeEquals(password, _config.BasicAuth.Password);
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
                return System.Text.Encoding.ASCII.GetString(value.ToArray());
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
}
