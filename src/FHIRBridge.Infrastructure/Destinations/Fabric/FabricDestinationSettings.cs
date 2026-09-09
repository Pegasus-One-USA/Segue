using System.Text.RegularExpressions;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations.Fabric;

/// <summary>
/// Parsed, non-secret configuration for a <see cref="Domain.Enums.DestinationType.DataFabricAzure"/> destination,
/// read from the same flat <c>dest_*</c>-keyed <see cref="DestinationConfiguration.ConnectionMetadataJson"/> bag
/// every other writer uses — no schema migration to add this destination type, exactly as
/// <see cref="Blob.BlobDestinationSettings"/> established. The client secret (service-principal mode) lives only
/// in <see cref="DestinationConfiguration.SecretReference"/>'s Key Vault entry.
///
/// OneLake addressing, which the rest of this class exists to build: a workspace behaves as the storage
/// *container* and an item as the first path segment, so
/// <c>https://onelake.blob.fabric.microsoft.com/{workspace}/{item}.Lakehouse/Files/{path}</c> is reachable with the
/// ordinary blob SDK against an Entra token. That is what lets this destination reuse the blob client machinery
/// instead of carrying a second storage stack.
/// </summary>
public sealed record FabricDestinationSettings(
    FabricLandingMode Mode,
    FabricAuthMode AuthMode,
    string Workspace,
    string ItemName,
    string ItemType,
    string BasePath,
    FabricFileFormat FileFormat,
    FabricPartitionScheme Partitioning,
    string? TenantId,
    string? ClientId,
    string? ManagedIdentityClientId,
    string? AuthorityHost,
    string EndpointSuffix,
    string? AccountUrlOverride)
{
    /// <summary>Only service-principal mode has credential material in Key Vault to resolve.</summary>
    public bool RequiresSecret => AuthMode == FabricAuthMode.ServicePrincipal;

    /// <summary>
    /// The OneLake blob-protocol account URL. The blob endpoint (not <c>dfs</c>) is deliberate: it is what the
    /// <c>Azure.Storage.Blobs</c> client speaks, and OneLake serves both.
    /// </summary>
    public string AccountUrl => AccountUrlOverride ?? $"https://onelake.blob.{EndpointSuffix}";

    /// <summary>The item's own path prefix inside the workspace container, e.g. <c>Sales.Lakehouse</c>.</summary>
    public string ItemPathSegment => $"{ItemName}.{ItemType}";

    /// <summary>Full prefix every written file sits under, e.g. <c>Sales.Lakehouse/Files/fhirbridge</c>.</summary>
    public string RootPath => string.IsNullOrWhiteSpace(BasePath)
        ? $"{ItemPathSegment}/Files"
        : $"{ItemPathSegment}/Files/{BasePath}";

    public string FileExtension => FileFormat switch
    {
        FabricFileFormat.Parquet => "parquet",
        FabricFileFormat.Csv => "csv",
        _ => "ndjson",
    };

    public string ContentType => FileFormat switch
    {
        FabricFileFormat.Parquet => "application/vnd.apache.parquet",
        FabricFileFormat.Csv => "text/csv",
        _ => "application/x-ndjson",
    };

    // A Fabric workspace/item name is far more permissive than an Azure container name (spaces and mixed case are
    // normal), so only genuinely unusable characters are rejected: path separators, which would silently retarget
    // the write at a different item, and control characters.
    private static readonly Regex UnsafeNameCharacters = new(@"[/\\\x00-\x1F\x7F]", RegexOptions.Compiled);

    private static readonly string[] SupportedItemTypes = ["Lakehouse", "Warehouse", "KQLDatabase", "MirroredDatabase"];

    public static FabricDestinationSettings Parse(DestinationConfiguration destination)
    {
        var json = destination.ConnectionMetadataJson;

        var mode = ParseEnum(ConnectionMetadataReader.GetString(json, "dest_fabricMode"), FabricLandingMode.OneLakeFiles);
        if (mode != FabricLandingMode.OneLakeFiles)
        {
            // Fail at configuration-parse time with the actual reason and the actual alternative, rather than
            // accepting the destination and writing nothing (or writing files a Warehouse will never read).
            throw new NotSupportedException(
                mode == FabricLandingMode.Eventstream
                    ? $"Destination '{destination.Name}': Fabric Eventstream is not implemented as a Fabric landing "
                        + "mode. An Eventstream custom endpoint is authenticated HTTP — use the Data Lake Webhook "
                        + "destination with that endpoint URL instead."
                    : $"Destination '{destination.Name}': Fabric landing mode '{mode}' is not implemented yet. Use "
                        + "OneLake Files, and promote to a table with a Fabric shortcut, notebook or pipeline.");
        }

        // Workspace comes from metadata, or from Target for a row created through the "existing connection" path —
        // the same dual read BlobDestinationSettings does for its container name.
        var workspace = FirstNonBlank(
            ConnectionMetadataReader.GetString(json, "dest_fabricWorkspace"),
            destination.Target);
        RequireName(workspace, "workspace (Target or dest_fabricWorkspace)", destination.Name);

        var itemName = ConnectionMetadataReader.GetString(json, "dest_fabricItemName");
        RequireName(itemName, "lakehouse/item name (dest_fabricItemName)", destination.Name);

        var itemType = ConnectionMetadataReader.GetString(json, "dest_fabricItemType") ?? "Lakehouse";
        var canonicalItemType = SupportedItemTypes.FirstOrDefault(
            supported => string.Equals(supported, itemType, StringComparison.OrdinalIgnoreCase));
        if (canonicalItemType is null)
        {
            throw new InvalidOperationException(
                $"Destination '{destination.Name}' has unsupported Fabric item type '{itemType}'. Supported: "
                    + string.Join(", ", SupportedItemTypes) + ".");
        }

        var authMode = ParseEnum(
            ConnectionMetadataReader.GetString(json, "dest_fabricAuthMode"), FabricAuthMode.ManagedIdentity);

        var tenantId = ConnectionMetadataReader.GetString(json, "dest_fabricTenantId");
        var clientId = ConnectionMetadataReader.GetString(json, "dest_fabricClientId");
        if (authMode == FabricAuthMode.ServicePrincipal)
        {
            RequireName(tenantId, "tenant id (dest_fabricTenantId)", destination.Name);
            RequireName(clientId, "client id (dest_fabricClientId)", destination.Name);
        }

        return new FabricDestinationSettings(
            Mode: mode,
            AuthMode: authMode,
            Workspace: workspace!.Trim(),
            ItemName: itemName!.Trim(),
            ItemType: canonicalItemType,
            BasePath: NormalizeBasePath(
                ConnectionMetadataReader.GetString(json, "dest_fabricPath"), destination.Name),
            FileFormat: ParseEnum(
                ConnectionMetadataReader.GetString(json, "dest_fabricFileFormat"), FabricFileFormat.Ndjson),
            Partitioning: ParseEnum(
                ConnectionMetadataReader.GetString(json, "dest_fabricPartitionBy"), FabricPartitionScheme.ResourceType),
            TenantId: tenantId,
            ClientId: clientId,
            ManagedIdentityClientId: ConnectionMetadataReader.GetString(json, "dest_fabricManagedIdentityClientId"),
            AuthorityHost: ConnectionMetadataReader.GetString(json, "dest_fabricAuthorityHost"),
            EndpointSuffix: ConnectionMetadataReader.GetString(json, "dest_fabricEndpointSuffix") ?? "fabric.microsoft.com",
            AccountUrlOverride: ConnectionMetadataReader.GetString(json, "dest_fabricAccountUrl"));
    }

    /// <summary>
    /// Normalizes the configured path to a clean relative path under the item's <c>Files/</c> area, and refuses the
    /// two shapes that would produce a broken result:
    /// <list type="bullet">
    /// <item><description><c>Tables/...</c> — a Fabric Lakehouse table is a Delta table, defined by its
    /// <c>_delta_log</c> transaction log. Dropping bare Parquet or NDJSON into <c>Tables/</c> does not register a
    /// table: it produces an unmanaged folder Fabric reports as an unidentified area, and the customer sees a table
    /// that never appears. This writer does not produce a Delta log, so the path is rejected with that explanation
    /// instead of appearing to succeed.</description></item>
    /// <item><description>An absolute URL or a path re-stating <c>{item}.Lakehouse</c> — the caller is trying to
    /// address the item twice, which would nest a second item folder inside the first.</description></item>
    /// </list>
    /// </summary>
    internal static string NormalizeBasePath(string? configuredPath, string destinationName)
    {
        var path = (configuredPath ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        if (path.Length == 0)
        {
            return "fhirbridge";
        }

        if (path.Contains("://", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Destination '{destinationName}': dest_fabricPath must be a relative path under the item's Files "
                    + $"area, not a full URL ('{configuredPath}').");
        }

        // Tolerate a path the user pasted from the Fabric UI with a leading "Files/" already on it.
        if (path.StartsWith("Files/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["Files/".Length..].Trim('/');
        }

        if (path.Equals("Tables", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("Tables/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Destination '{destinationName}': the Lakehouse 'Tables/' area holds Delta tables, and this "
                    + "destination writes plain files (no Delta transaction log), so files landed there would never "
                    + "register as a table. Write to the Files area instead and surface it as a table with a Fabric "
                    + "shortcut, notebook or pipeline.");
        }

        if (path.Contains(".Lakehouse", StringComparison.OrdinalIgnoreCase)
            || path.Contains(".Warehouse", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Destination '{destinationName}': dest_fabricPath must not repeat the item name — the item is "
                    + "already addressed by dest_fabricItemName/dest_fabricItemType.");
        }

        if (path.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException(
                $"Destination '{destinationName}': dest_fabricPath must not contain '.' or '..' segments.");
        }

        return path;
    }

    private static void RequireName(string? value, string label, string destinationName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Destination '{destinationName}' has no {label} configured.");
        }

        if (UnsafeNameCharacters.IsMatch(value))
        {
            throw new InvalidOperationException(
                $"Destination '{destinationName}' has an invalid {label}: '{value}' — it cannot contain a path "
                    + "separator or control characters.");
        }
    }

    private static TEnum ParseEnum<TEnum>(string? raw, TEnum fallback)
        where TEnum : struct, Enum
        => Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed) ? parsed : fallback;

    private static string? FirstNonBlank(params string?[] candidates)
        => candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
}
