using System.Security.Cryptography;
using System.Text;
using Azure.Core;
using Azure.Identity;
using FHIRBridge.Runtime.Application.Abstractions.Auth;

namespace FHIRBridge.Infrastructure.Destinations.Auth;

/// <summary>
/// Managed-identity token acquisition for a FHIR destination (e.g. Azure Health Data Services), mirroring
/// <see cref="BlobContainerClientFactory"/>'s <c>BuildFromManagedIdentity</c> exactly (same
/// <see cref="DefaultAzureCredential"/>/<see cref="DefaultAzureCredentialOptions"/> shape) but for a bearer token
/// rather than a Storage SDK client. Reuses the same <see cref="IFhirAccessTokenCache"/> the client-credentials
/// provider shares, key-prefixed "fhir-dest-token:managed-identity:" so it can never collide with an OAuth2
/// client-credentials cache entry for the same destination.
/// </summary>
public sealed class AzureManagedIdentityFhirTokenProvider : IAzureManagedIdentityFhirTokenProvider
{
    private readonly IFhirAccessTokenCache _tokenCache;

    public AzureManagedIdentityFhirTokenProvider(IFhirAccessTokenCache tokenCache)
    {
        _tokenCache = tokenCache;
    }

    public async Task<string> GetAccessTokenAsync(
        string resourceScope,
        string? managedIdentityClientId,
        string? authorityHost,
        CancellationToken cancellationToken)
    {
        var cacheKey = BuildCacheKey(resourceScope, managedIdentityClientId, authorityHost);
        var cached = await _tokenCache.GetAsync(cacheKey, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = managedIdentityClientId,
            AuthorityHost = ParseAuthorityHost(authorityHost),
        });

        var token = await credential.GetTokenAsync(
            new TokenRequestContext([resourceScope]), cancellationToken);

        await _tokenCache.SetAsync(cacheKey, token.Token, token.ExpiresOn, cancellationToken);
        return token.Token;
    }

    private static Uri? ParseAuthorityHost(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && Uri.TryCreate(raw, UriKind.Absolute, out var uri) ? uri : null;

    // No secret is ever involved in this flow, so (unlike the client-credentials provider) there is nothing to
    // fingerprint — the key is just the parameters that determine which identity/scope the token was issued for.
    private static string BuildCacheKey(string resourceScope, string? managedIdentityClientId, string? authorityHost)
    {
        var raw = $"{resourceScope}|{managedIdentityClientId}|{authorityHost}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
        return $"fhir-dest-token:managed-identity|{hash}";
    }
}
