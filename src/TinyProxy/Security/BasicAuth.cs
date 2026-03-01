using System.Security.Cryptography;

namespace TinyProxy.Security;

/// <summary>
/// HTTP Basic Authentication validator.
/// Supports multiple users with constant-time comparison for security.
/// Stores passwords as byte arrays for consistent encoding.
/// </summary>
public sealed class BasicAuth
{
    private readonly Configuration _config;
    private readonly List<byte[]> _encodedTokens;

    /// <summary>
    /// Initializes a new instance of the <see cref="BasicAuth"/> class.
    /// </summary>
    public BasicAuth(Configuration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _encodedTokens = new List<byte[]>();

        if (_config.BasicAuth != null)
            AddEncodedToken(_config.BasicAuth.Username, _config.BasicAuth.Password);

        if (_config.BasicAuthUsers != null)
            foreach (var user in _config.BasicAuthUsers)
                AddEncodedToken(user.Username, user.Password);
    }

    /// <summary>
    /// Validates the Authorization header against configured credentials.
    /// Uses constant-time comparison to prevent timing attacks.
    /// </summary>
    public bool Validate(string? authorizationHeader)
    {
        if (_encodedTokens.Count == 0) return true;

        if (string.IsNullOrWhiteSpace(authorizationHeader)) return false;
        if (!authorizationHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;

        var base64Part = authorizationHeader.AsSpan(6).Trim();
        if (base64Part.IsEmpty) return false;
        var candidate = Encoding.ASCII.GetBytes(base64Part.ToString());

        var matched = false;
        foreach (var token in _encodedTokens)
        {
            if (token.Length != candidate.Length) continue;

            if (CryptographicOperations.FixedTimeEquals(candidate, token))
                matched = true;
        }

        CryptographicOperations.ZeroMemory(candidate);
        return matched;
    }

    /// <summary>
    /// Extracts Authorization header from the request headers.
    /// </summary>
    public static string? GetAuthorizationHeader(IDictionary<string, ReadOnlySequence<byte>> headers)
    {
        if (headers.TryGetValue("Proxy-Authorization", out var value) ||
            headers.TryGetValue("Authorization", out value))
            if (value.Length > 0)
            {
                var span = value.IsSingleSegment ? value.FirstSpan : value.ToArray();
                return Encoding.ASCII.GetString(span);
            }

        return null;
    }

    /// <summary>
    /// Gets realm.
    /// </summary>
    public string GetRealm()
    {
        return _config.BasicAuth?.Realm ?? "TinyProxy";
    }

    private void AddEncodedToken(string? username, string? password)
    {
        if (string.IsNullOrEmpty(username) || password == null) return;

        var raw = $"{username}:{password}";
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        _encodedTokens.Add(Encoding.ASCII.GetBytes(token));
    }
}