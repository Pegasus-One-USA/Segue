using System.Collections.Concurrent;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

/// <summary>
/// Process-local token store for interactive authorization-code grants. Adequate for single-instance deployments and
/// tests; a production HA deployment would swap in a distributed implementation (Key Vault / Redis) behind the same
/// <see cref="IFhirAuthorizationCodeTokenStore"/> contract.
/// </summary>
public sealed class InMemoryFhirAuthorizationCodeTokenStore : IFhirAuthorizationCodeTokenStore
{
    private readonly ConcurrentDictionary<string, StoredOAuthToken> _tokens = new(StringComparer.Ordinal);

    public Task<StoredOAuthToken?> GetAsync(string key, CancellationToken cancellationToken)
    {
        _tokens.TryGetValue(key, out var token);
        return Task.FromResult(token);
    }

    public Task SaveAsync(string key, StoredOAuthToken token, CancellationToken cancellationToken)
    {
        _tokens[key] = token;
        return Task.CompletedTask;
    }
}
