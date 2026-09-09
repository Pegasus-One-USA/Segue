using System.Security.Cryptography;
using System.Text;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Destinations.Blob;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Destinations.Fabric;

/// <summary>
/// Builds (and caches, via the same singleton <see cref="BlobContainerClientCache"/> the Blob Storage destination
/// uses) the workspace-scoped client for a Fabric destination.
///
/// OneLake speaks the ADLS/blob protocol with a Fabric-specific address mapping — workspace as container, item as
/// the leading path segment — so the ordinary <c>Azure.Storage.Blobs</c> client works against it unchanged. What
/// does NOT carry over is auth: OneLake accepts only Entra tokens, never an account key or SAS, which is why this
/// factory has two modes where <see cref="BlobContainerClientFactory"/> has five.
/// </summary>
internal sealed class OneLakeClientFactory : IOneLakeClientFactory
{
    private readonly ISecretProvider _secretProvider;
    private readonly BlobContainerClientCache _cache;
    private readonly ILogger<OneLakeClientFactory> _logger;

    public OneLakeClientFactory(
        ISecretProvider secretProvider, BlobContainerClientCache cache, ILogger<OneLakeClientFactory> logger)
    {
        _secretProvider = secretProvider;
        _cache = cache;
        _logger = logger;
    }

    public async Task<BlobDestinationTarget> GetWorkspaceAsync(
        DestinationConfiguration destination,
        FabricDestinationSettings settings,
        CancellationToken cancellationToken)
    {
        var secret = settings.RequiresSecret
            ? await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken)
            : null;

        // Hashing the resolved secret into the key (never storing it) means a rotated client secret invalidates the
        // cached client automatically — same rationale as BlobContainerClientFactory.BuildCacheKey.
        var secretHash = secret is null
            ? "none"
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)))[..16];
        var cacheKey = string.Join(
            '|', "onelake", destination.Id, settings.AuthMode, settings.AccountUrl, settings.Workspace, secretHash);

        return _cache.GetOrAdd(cacheKey, () => Build(destination, settings, secret));
    }

    private BlobDestinationTarget Build(
        DestinationConfiguration destination, FabricDestinationSettings settings, string? secret)
    {
        _logger.LogDebug(
            "Building OneLake client for destination {DestinationId} ({DestinationName}), auth mode {AuthMode}, "
                + "workspace {Workspace}, item {Item}.",
            destination.Id,
            destination.Name,
            settings.AuthMode,
            settings.Workspace,
            settings.ItemPathSegment);

        var credential = BuildCredential(settings, secret);
        var container = new BlobServiceClient(new Uri(settings.AccountUrl), credential)
            .GetBlobContainerClient(settings.Workspace);

        // Always false: the "container" here is a Fabric workspace. It is provisioned in Fabric (or by the Fabric
        // REST API), and a storage-protocol create against it can only fail.
        return new BlobDestinationTarget(container, SupportsContainerCreate: false);
    }

    private static TokenCredential BuildCredential(FabricDestinationSettings settings, string? secret)
        => settings.AuthMode switch
        {
            FabricAuthMode.ServicePrincipal => new ClientSecretCredential(
                settings.TenantId,
                settings.ClientId,
                secret,
                new ClientSecretCredentialOptions { AuthorityHost = ParseAuthorityHost(settings.AuthorityHost) }),

            FabricAuthMode.ManagedIdentity => new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = settings.ManagedIdentityClientId,
                AuthorityHost = ParseAuthorityHost(settings.AuthorityHost),
            }),

            _ => throw new InvalidOperationException($"Unsupported Fabric auth mode '{settings.AuthMode}'."),
        };

    private static Uri? ParseAuthorityHost(string? raw)
        => !string.IsNullOrWhiteSpace(raw) && Uri.TryCreate(raw, UriKind.Absolute, out var uri) ? uri : null;
}
