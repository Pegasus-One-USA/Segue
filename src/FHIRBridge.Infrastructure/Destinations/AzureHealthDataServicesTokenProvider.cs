using Azure.Core;
using Azure.Identity;
using FHIRBridge.Runtime.Application.Abstractions.Auth;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Client-credentials mode targets a service principal registered in the <em>client's own</em> Entra ID tenant
/// (this is a SaaS write-back into a customer-owned Azure subscription, not one FHIRBridge manages) — so a client
/// secret is always required for that mode. Managed-identity mode is only viable when FHIRBridge itself runs inside
/// the target Azure subscription; it uses the host's own identity and needs no secret. Tokens are cached via the
/// same distributed <see cref="IFhirAccessTokenCache"/> the source-side FHIR connectors use, so the API and Worker
/// share cached tokens and re-acquire only shortly before real expiry.
/// </summary>
internal sealed class AzureHealthDataServicesTokenProvider : IAzureHealthDataServicesTokenProvider
{
    private readonly IFhirAccessTokenCache _tokenCache;

    public AzureHealthDataServicesTokenProvider(IFhirAccessTokenCache tokenCache)
    {
        _tokenCache = tokenCache;
    }

    public async Task<string> GetAccessTokenAsync(
        AzureHealthDataServicesConnectionOptions options,
        string? clientSecret,
        CancellationToken cancellationToken)
    {
        var scope = options.ResolveScope();
        var cacheKey = BuildCacheKey(options, scope);

        var cached = await _tokenCache.GetAsync(cacheKey, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        var credential = BuildCredential(options, clientSecret);
        var token = await credential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken);

        await _tokenCache.SetAsync(cacheKey, token.Token, token.ExpiresOn, cancellationToken);
        return token.Token;
    }

    private static TokenCredential BuildCredential(AzureHealthDataServicesConnectionOptions options, string? clientSecret)
    {
        if (options.IsManagedIdentity)
        {
            var identity = string.IsNullOrWhiteSpace(options.ManagedIdentityClientId)
                ? ManagedIdentityId.SystemAssigned
                : ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId);
            return new ManagedIdentityCredential(identity);
        }

        if (string.IsNullOrWhiteSpace(options.TenantId) || string.IsNullOrWhiteSpace(options.ClientId))
        {
            throw new InvalidOperationException(
                "Azure Health Data Services client-credentials auth requires a tenant id and client id.");
        }

        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException(
                "Azure Health Data Services client-credentials auth requires a client secret.");
        }

        return new ClientSecretCredential(options.TenantId, options.ClientId, clientSecret);
    }

    private static string BuildCacheKey(AzureHealthDataServicesConnectionOptions options, string scope) =>
        options.IsManagedIdentity
            ? $"ahds-token:managed-identity|{options.FhirServiceUrl}|{options.ManagedIdentityClientId}|{scope}"
            : $"ahds-token:client-credentials|{options.FhirServiceUrl}|{options.TenantId}|{options.ClientId}|{scope}";
}
