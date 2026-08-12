using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations.Blob;

/// <summary>
/// Parsed, non-secret configuration for a <see cref="Domain.Enums.DestinationType.BlobStorage"/> destination —
/// everything except the credential material itself, which lives in <see cref="DestinationConfiguration.SecretReference"/>
/// and is resolved separately (see <see cref="RequiresSecret"/> on why Managed Identity never needs it). Read
/// from the same flat <c>dest_*</c>-keyed <see cref="DestinationConfiguration.ConnectionMetadataJson"/>
/// bag every other writer's wizard fields use — no schema change needed to add these fields.
/// </summary>
public sealed record BlobDestinationSettings(
    BlobDestinationAuthMode AuthMode,
    string ContainerName,
    string? AccountUrl,
    string? AccountName,
    string EndpointSuffix,
    string? TenantId,
    string? ClientId,
    string? ManagedIdentityClientId,
    string? AuthorityHost,
    string? PathPrefix,
    bool CreateContainerIfNotExists,
    string? AccessTier)
{
    /// <summary>Every mode except Managed Identity needs the resolved Key Vault secret as credential material.</summary>
    public bool RequiresSecret => AuthMode != BlobDestinationAuthMode.ManagedIdentity;

    public static BlobDestinationSettings Parse(DestinationConfiguration destination)
    {
        var json = destination.ConnectionMetadataJson;
        var authMode = ParseAuthMode(ConnectionMetadataReader.GetString(json, "dest_blobAuthMode"));

        var containerName = !string.IsNullOrWhiteSpace(destination.Target)
            ? destination.Target
            : ConnectionMetadataReader.GetString(json, "dest_blobContainer");
        if (string.IsNullOrWhiteSpace(containerName))
        {
            throw new InvalidOperationException(
                $"Destination '{destination.Name}' has no blob container configured (Target or dest_blobContainer).");
        }

        var accountName = ConnectionMetadataReader.GetString(json, "dest_blobAccountName");
        var endpointSuffix = ConnectionMetadataReader.GetString(json, "dest_blobEndpointSuffix") ?? "core.windows.net";
        var accountUrl = ConnectionMetadataReader.GetString(json, "dest_blobAccountUrl");
        if (string.IsNullOrWhiteSpace(accountUrl) && !string.IsNullOrWhiteSpace(accountName))
        {
            accountUrl = $"https://{accountName}.blob.{endpointSuffix}";
        }

        var tenantId = ConnectionMetadataReader.GetString(json, "dest_blobTenantId");
        var clientId = ConnectionMetadataReader.GetString(json, "dest_blobClientId");

        switch (authMode)
        {
            case BlobDestinationAuthMode.AccountKey when string.IsNullOrWhiteSpace(accountName):
                throw new InvalidOperationException(
                    $"Destination '{destination.Name}' uses Account Key auth but has no dest_blobAccountName configured.");
            case BlobDestinationAuthMode.ManagedIdentity when string.IsNullOrWhiteSpace(accountUrl):
                throw new InvalidOperationException(
                    $"Destination '{destination.Name}' uses Managed Identity auth but has no dest_blobAccountUrl/dest_blobAccountName configured.");
            case BlobDestinationAuthMode.ServicePrincipal when string.IsNullOrWhiteSpace(accountUrl):
                throw new InvalidOperationException(
                    $"Destination '{destination.Name}' uses Service Principal auth but has no dest_blobAccountUrl/dest_blobAccountName configured.");
            case BlobDestinationAuthMode.ServicePrincipal when string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId):
                throw new InvalidOperationException(
                    $"Destination '{destination.Name}' uses Service Principal auth but is missing dest_blobTenantId/dest_blobClientId.");
        }

        return new BlobDestinationSettings(
            AuthMode: authMode,
            ContainerName: containerName,
            AccountUrl: accountUrl,
            AccountName: accountName,
            EndpointSuffix: endpointSuffix,
            TenantId: tenantId,
            ClientId: clientId,
            ManagedIdentityClientId: ConnectionMetadataReader.GetString(json, "dest_blobManagedIdentityClientId"),
            AuthorityHost: ConnectionMetadataReader.GetString(json, "dest_blobAuthorityHost"),
            PathPrefix: ConnectionMetadataReader.GetString(json, "dest_blobPathPrefix")?.Trim('/'),
            CreateContainerIfNotExists: ParseBool(ConnectionMetadataReader.GetString(json, "dest_blobCreateContainerIfNotExists"), true),
            AccessTier: ConnectionMetadataReader.GetString(json, "dest_blobAccessTier"));
    }

    private static BlobDestinationAuthMode ParseAuthMode(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "accountkey" => BlobDestinationAuthMode.AccountKey,
        "sasurl" => BlobDestinationAuthMode.SasUrl,
        "managedidentity" => BlobDestinationAuthMode.ManagedIdentity,
        "serviceprincipal" => BlobDestinationAuthMode.ServicePrincipal,
        _ => BlobDestinationAuthMode.ConnectionString,
    };

    private static bool ParseBool(string? raw, bool fallback) => bool.TryParse(raw, out var value) ? value : fallback;
}
