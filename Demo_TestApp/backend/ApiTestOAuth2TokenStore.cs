using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace HealthAppBackend;

/// <summary>
/// Opaque access tokens issued by <c>POST /api/apitest/oauth/token</c> and validated by
/// <c>POST /api/apitest/auth/oauth2</c> — the ApiEndpoint destination's OAuth2ClientCredentials mode counterpart to
/// <see cref="DataLakeTokenStore"/> (same reasoning: no real identity provider is worth standing up for a demo, so
/// this app issues and verifies its own token). Kept as its own store rather than reusing DataLakeTokenStore so the
/// two token surfaces (Data Lake Webhook vs. ApiEndpoint) can be cleared/inspected independently.
/// </summary>
public sealed class ApiTestOAuth2TokenStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _tokens = new(StringComparer.Ordinal);

    public string Issue(TimeSpan lifetime)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _tokens[token] = DateTimeOffset.UtcNow.Add(lifetime);
        Prune();
        return token;
    }

    public bool IsValid(string token)
        => _tokens.TryGetValue(token, out var expiresAt) && expiresAt > DateTimeOffset.UtcNow;

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _tokens)
        {
            if (entry.Value <= now)
            {
                _tokens.TryRemove(entry.Key, out _);
            }
        }
    }
}
