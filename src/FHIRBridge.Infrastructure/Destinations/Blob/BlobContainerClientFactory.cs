using System.Security.Cryptography;
using System.Text;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Destinations.Blob;

/// <summary>
/// Builds (and caches, via <see cref="BlobContainerClientCache"/>) the <see cref="BlobContainerClient"/> for a
/// blob destination, dispatching on <see cref="BlobDestinationSettings.AuthMode"/>. Scoped because it depends on
/// the (scoped) <see cref="ISecretProvider"/>; the underlying client cache is a separate singleton so clients
/// still survive across request scopes.
/// </summary>
internal sealed class BlobContainerClientFactory : IBlobContainerClientFactory
{
    private readonly ISecretProvider _secretProvider;
    private readonly BlobContainerClientCache _cache;
    private readonly ILogger<BlobContainerClientFactory> _logger;

    public BlobContainerClientFactory(
        ISecretProvider secretProvider, BlobContainerClientCache cache, ILogger<BlobContainerClientFactory> logger)
    {
        _secretProvider = secretProvider;
        _cache = cache;
        _logger = logger;
    }

    public async Task<BlobDestinationTarget> GetTargetAsync(
        DestinationConfiguration destination, BlobDestinationSettings settings, CancellationToken cancellationToken)
    {
        // Managed Identity never resolves a secret — see BlobDestinationSettings.RequiresSecret.
        var secret = settings.RequiresSecret
            ? await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken)
            : null;

        var cacheKey = BuildCacheKey(destination, settings, secret);

        // Hashing the resolved secret into the key (never storing it) means a rotated Key Vault secret or SAS
        // token invalidates the cached client automatically — no restart or manual cache-bust needed.
        return _cache.GetOrAdd(cacheKey, () => Build(destination, settings, secret));
    }

    private BlobDestinationTarget Build(DestinationConfiguration destination, BlobDestinationSettings settings, string? secret)
    {
        _logger.LogDebug(
            "Building blob client for destination {DestinationId} ({DestinationName}), auth mode {AuthMode}, container {Container}.",
            destination.Id, destination.Name, settings.AuthMode, settings.ContainerName);

        // A row saved before auth-mode metadata existed has its secret set to the legacy pre-signed SAS PUT URL —
        // coerce it to SasUrl instead of misreading it as a connection string.
        var authMode = settings.AuthMode == BlobDestinationAuthMode.ConnectionString
            && secret is not null
            && Uri.TryCreate(secret, UriKind.Absolute, out var legacyUri)
            && (legacyUri.Scheme == Uri.UriSchemeHttp || legacyUri.Scheme == Uri.UriSchemeHttps)
                ? BlobDestinationAuthMode.SasUrl
                : settings.AuthMode;

        return authMode switch
        {
            BlobDestinationAuthMode.ConnectionString => BuildFromConnectionString(settings, secret!),
            BlobDestinationAuthMode.AccountKey => BuildFromAccountKey(settings, secret!),
            BlobDestinationAuthMode.SasUrl => BuildFromSasUrl(settings, secret!),
            BlobDestinationAuthMode.ManagedIdentity => BuildFromManagedIdentity(settings),
            BlobDestinationAuthMode.ServicePrincipal => BuildFromServicePrincipal(settings, secret!),
            _ => throw new InvalidOperationException($"Unsupported blob auth mode '{authMode}'."),
        };
    }

    private static BlobDestinationTarget BuildFromConnectionString(BlobDestinationSettings settings, string connectionString)
    {
        var client = new BlobServiceClient(connectionString).GetBlobContainerClient(settings.ContainerName);
        return new BlobDestinationTarget(client, SupportsContainerCreate: true);
    }

    private static BlobDestinationTarget BuildFromAccountKey(BlobDestinationSettings settings, string accountKey)
    {
        if (string.IsNullOrWhiteSpace(settings.AccountUrl))
        {
            throw new InvalidOperationException("Account Key auth requires an account URL or account name.");
        }

        var credential = new StorageSharedKeyCredential(settings.AccountName!, accountKey);
        var client = new BlobServiceClient(new Uri(settings.AccountUrl), credential).GetBlobContainerClient(settings.ContainerName);
        return new BlobDestinationTarget(client, SupportsContainerCreate: true);
    }

    private static BlobDestinationTarget BuildFromSasUrl(BlobDestinationSettings settings, string sas)
    {
        if (Uri.TryCreate(sas, UriKind.Absolute, out var sasUri))
        {
            // A container-scoped SAS's path is "/{container}[/...]"; an account/service SAS has no path segment.
            var hasContainerSegment = sasUri.AbsolutePath.Trim('/').Length > 0;
            if (hasContainerSegment)
            {
                return new BlobDestinationTarget(new BlobContainerClient(sasUri), SupportsContainerCreate: false);
            }

            var serviceScopedClient = new BlobServiceClient(sasUri).GetBlobContainerClient(settings.ContainerName);
            return new BlobDestinationTarget(serviceScopedClient, SupportsContainerCreate: true);
        }

        // A bare token ("sv=...&sig=...") rather than a full URL — combine it with the configured account URL.
        if (string.IsNullOrWhiteSpace(settings.AccountUrl))
        {
            throw new InvalidOperationException(
                "A bare SAS token requires dest_blobAccountUrl/dest_blobAccountName to be configured.");
        }

        var combined = new Uri($"{settings.AccountUrl.TrimEnd('/')}?{sas.TrimStart('?')}");
        var client = new BlobServiceClient(combined).GetBlobContainerClient(settings.ContainerName);
        return new BlobDestinationTarget(client, SupportsContainerCreate: true);
    }

    private static BlobDestinationTarget BuildFromManagedIdentity(BlobDestinationSettings settings)
    {
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = settings.ManagedIdentityClientId,
            AuthorityHost = ParseAuthorityHost(settings.AuthorityHost),
        });
        var client = new BlobServiceClient(new Uri(settings.AccountUrl!), credential).GetBlobContainerClient(settings.ContainerName);
        return new BlobDestinationTarget(client, SupportsContainerCreate: true);
    }

    private static BlobDestinationTarget BuildFromServicePrincipal(BlobDestinationSettings settings, string clientSecret)
    {
        var options = new ClientSecretCredentialOptions { AuthorityHost = ParseAuthorityHost(settings.AuthorityHost) };
        var credential = new ClientSecretCredential(settings.TenantId, settings.ClientId, clientSecret, options);
        var client = new BlobServiceClient(new Uri(settings.AccountUrl!), credential).GetBlobContainerClient(settings.ContainerName);
        return new BlobDestinationTarget(client, SupportsContainerCreate: true);
    }

    private static Uri? ParseAuthorityHost(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && Uri.TryCreate(raw, UriKind.Absolute, out var uri) ? uri : null;

    /// <summary>
    /// Keys the cache on everything that determines client identity — including a hash of the resolved secret
    /// (never the secret itself) so a rotated key/SAS/client-secret naturally busts the cache.
    /// </summary>
    private static string BuildCacheKey(DestinationConfiguration destination, BlobDestinationSettings settings, string? secret)
    {
        var secretHash = secret is null
            ? "none"
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)))[..16];

        return string.Join('|', destination.Id, settings.AuthMode, settings.ContainerName, settings.AccountUrl, secretHash);
    }
}
