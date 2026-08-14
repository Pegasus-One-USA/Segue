using System.Text.RegularExpressions;
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
    string? AccessTier,
    BlobDeliveryGranularity Granularity,
    BlobRecordMode RecordMode,
    string FolderPattern,
    string FileNamePattern)
{
    /// <summary>Every mode except Managed Identity needs the resolved Key Vault secret as credential material.</summary>
    public bool RequiresSecret => AuthMode != BlobDestinationAuthMode.ManagedIdentity;

    private static readonly Regex ContainerNameRegex = new(
        @"^(?!.*--)[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$", RegexOptions.Compiled);

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

        // Azure rejects an invalid container name with an opaque "InvalidResourceName" 400 at write time — the
        // request-level validator (CreateDestinationConfigurationRequestValidator) catches this for new/edited
        // destinations, but this is the last line of defense for a legacy row saved before that check existed.
        if (!ContainerNameRegex.IsMatch(containerName))
        {
            throw new InvalidOperationException(
                $"'{containerName}' is not a valid Azure Blob container name for destination '{destination.Name}' — "
                    + "use 3-63 characters: lowercase letters, numbers, and single hyphens only "
                    + "(no leading, trailing, or double hyphens).");
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
            AccessTier: ConnectionMetadataReader.GetString(json, "dest_blobAccessTier"),
            Granularity: ParseGranularity(json),
            RecordMode: ParseRecordMode(ConnectionMetadataReader.GetString(json, "dest_blobRecordMode")),
            FolderPattern: ParseFolderPattern(json),
            FileNamePattern: ParseFileNamePattern(json, ParseRecordMode(ConnectionMetadataReader.GetString(json, "dest_blobRecordMode"))));
    }

    /// <summary>Defaults to <c>{name}</c> — the pre-pattern behavior of one folder per mapped resource/object.</summary>
    private static string ParseFolderPattern(string? json)
    {
        var raw = ConnectionMetadataReader.GetString(json, "dest_blobFolderPattern");
        return string.IsNullOrWhiteSpace(raw) ? "{name}" : raw.Trim();
    }

    /// <summary>
    /// Insert defaults to a always-unique, timestamped name (its records are never looked up again). Upsert/Update
    /// default to a static, id-only name — deliberately excluding any <c>{date:...}</c>/<c>{guid}</c> token, since
    /// those modes must re-derive the same blob name on a later run to find the record to update.
    /// </summary>
    private static string ParseFileNamePattern(string? json, BlobRecordMode recordMode)
    {
        var raw = ConnectionMetadataReader.GetString(json, "dest_blobFileNamePattern");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return raw.Trim();
        }

        return recordMode == BlobRecordMode.Insert
            ? "{id}_{date:yyyyMMddHHmmssfff}_{guid}.json"
            : "{id}.json";
    }

    /// <summary>
    /// Reads the current <c>dest_blobGranularity</c> key first; falls back to the short-lived
    /// <c>dest_writeMode</c> key an earlier iteration of this feature used ("append"/"upsert") for any row
    /// saved during that window, so it doesn't silently change behavior. Defaults to Bulk either way — Bulk
    /// is Blob's original, only-ever-shipped behavior, so an unconfigured row must keep doing that.
    /// </summary>
    private static BlobDeliveryGranularity ParseGranularity(string? json)
    {
        var raw = ConnectionMetadataReader.GetString(json, "dest_blobGranularity")
            ?? ConnectionMetadataReader.GetString(json, "dest_writeMode");

        return raw?.Trim().ToLowerInvariant() switch
        {
            "individual" or "upsert" => BlobDeliveryGranularity.Individual,
            _ => BlobDeliveryGranularity.Bulk,
        };
    }

    private static BlobRecordMode ParseRecordMode(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "insert" => BlobRecordMode.Insert,
        "update" => BlobRecordMode.Update,
        _ => BlobRecordMode.Upsert,
    };

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
