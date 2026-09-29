using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations.Fabric;

/// <summary>
/// Parsed, non-secret configuration for a <see cref="Domain.Enums.DestinationType.CosmosDbFabric"/> destination,
/// read from the same flat <c>dest_*</c>-keyed metadata bag every other writer uses.
///
/// <para><b>Entra only.</b> Cosmos DB in Fabric "exclusively relies on Microsoft Entra ID authentication and
/// built-in data plane roles". There are no account keys to configure — which is why this has the same two-mode
/// auth shape as <see cref="FabricDestinationSettings"/> rather than the five-mode shape the Blob destination
/// offers, and why no connection string is ever assembled here.</para>
/// </summary>
public sealed record CosmosDbFabricDestinationSettings(
    FabricAuthMode AuthMode,
    string Endpoint,
    string Database,
    string? Container,
    string? TenantId,
    string? ClientId,
    string? ManagedIdentityClientId,
    string? AuthorityHost,
    string? PartitionKeyPath,
    CosmosContainerCreationMode ContainerCreationMode)
{
    /// <summary>The partition key <see cref="CosmosContainerCreationMode.UseDefaultPartitionKey"/> falls back to.</summary>
    internal const string DefaultPartitionKeyPath = "/id";

    /// <summary>Whether a missing container may be created at all.</summary>
    public bool CanCreateContainer => ContainerCreationMode != CosmosContainerCreationMode.Never;

    /// <summary>
    /// The partition key a newly created container gets, or null when this destination does not create them.
    /// Never guesses: the default only applies under the mode that explicitly asks for it.
    /// </summary>
    public string? PartitionKeyPathForNewContainer => ContainerCreationMode switch
    {
        CosmosContainerCreationMode.UseConfiguredPartitionKey => PartitionKeyPath,
        CosmosContainerCreationMode.UseDefaultPartitionKey => PartitionKeyPath ?? DefaultPartitionKeyPath,
        _ => null,
    };

    /// <summary>
    /// True when a container would be created on a key the user did not choose. The writer logs a warning for
    /// this on every create — the choice is irreversible, so it belongs in the run's own record rather than
    /// only on the screen where the box was ticked.
    /// </summary>
    public bool CreatesOnUnchosenPartitionKey =>
        ContainerCreationMode == CosmosContainerCreationMode.UseDefaultPartitionKey
            && string.IsNullOrWhiteSpace(PartitionKeyPath);

    /// <summary>Only a service principal resolves a Key Vault secret; managed identity carries none.</summary>
    public bool RequiresSecret => AuthMode == FabricAuthMode.ServicePrincipal;

    /// <summary>
    /// The container this write targets: the destination's explicit override, else the mapping's destination
    /// object with any schema qualifier and write-mode suffix stripped.
    ///
    /// <para>A container name is a flat identifier — "dbo.Patient" would create a container literally called
    /// that — so the last dot-separated segment is taken, the same rule the Lakehouse Delta surface applies to
    /// the same input. One mapping profile then names the same thing on either destination.</para>
    /// </summary>
    public string ResolveContainer(MappingProfile mappingProfile)
    {
        if (!string.IsNullOrWhiteSpace(Container))
        {
            return Container.Trim();
        }

        var stem = mappingProfile.DestinationObject ?? string.Empty;

        var suffixIndex = stem.IndexOf(';', StringComparison.Ordinal);
        if (suffixIndex >= 0)
        {
            stem = stem[..suffixIndex];
        }

        var parts = stem.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var name = parts.Length == 0 ? string.Empty : parts[^1];

        return name.Length == 0 ? mappingProfile.ResourceType : name;
    }

    public static CosmosDbFabricDestinationSettings Parse(DestinationConfiguration destination)
    {
        var json = destination.ConnectionMetadataJson;

        var endpoint = ConnectionMetadataReader.GetString(json, "dest_cosmosFabricEndpoint")?.Trim();
        Require(endpoint, "endpoint (dest_cosmosFabricEndpoint)", destination.Name);

        // Checked here rather than left to the SDK: CosmosClient's own failure for a malformed endpoint arrives
        // at write time, deep in a network call, and names the URI rather than the setting that produced it.
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
            || (endpointUri.Scheme != Uri.UriSchemeHttps && endpointUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                $"Destination '{destination.Name}': the Cosmos DB endpoint must be an absolute https URL "
                    + $"('{endpoint}'). Copy it from the database's Settings > Connection section in Fabric.");
        }

        var database = ConnectionMetadataReader.GetString(json, "dest_cosmosFabricDatabase")?.Trim();
        Require(database, "database name (dest_cosmosFabricDatabase)", destination.Name);

        var authMode = ParseAuthMode(ConnectionMetadataReader.GetString(json, "dest_cosmosFabricAuthMode"));

        var tenantId = NullIfBlank(ConnectionMetadataReader.GetString(json, "dest_cosmosFabricTenantId"));
        var clientId = NullIfBlank(ConnectionMetadataReader.GetString(json, "dest_cosmosFabricClientId"));
        if (authMode == FabricAuthMode.ServicePrincipal)
        {
            Require(tenantId, "tenant id (dest_cosmosFabricTenantId)", destination.Name);
            Require(clientId, "client id (dest_cosmosFabricClientId)", destination.Name);
        }

        var partitionKeyPath = NormalizePartitionKeyPath(
            ConnectionMetadataReader.GetString(json, "dest_cosmosFabricPartitionKeyPath"));

        var containerCreationMode = ParseContainerCreationMode(
            ConnectionMetadataReader.GetString(json, "dest_cosmosFabricContainerCreationMode"),
            partitionKeyPath,
            destination.Name);

        return new CosmosDbFabricDestinationSettings(
            AuthMode: authMode,
            Endpoint: endpoint!,
            Database: database!,
            // NullIfBlank throughout: the wizard posts every field it renders, so an untouched optional one
            // arrives as "" rather than absent. Normalising at parse time means no consumer downstream has to
            // know the difference — the same bug an empty account-url override once caused for OneLake.
            Container: NullIfBlank(ConnectionMetadataReader.GetString(json, "dest_cosmosFabricContainer")),
            TenantId: tenantId,
            ClientId: clientId,
            ManagedIdentityClientId: NullIfBlank(
                ConnectionMetadataReader.GetString(json, "dest_cosmosFabricManagedIdentityClientId")),
            AuthorityHost: NullIfBlank(ConnectionMetadataReader.GetString(json, "dest_cosmosFabricAuthorityHost")),
            PartitionKeyPath: partitionKeyPath,
            ContainerCreationMode: containerCreationMode);
    }

    /// <summary>
    /// Reads the container-creation mode, refusing "create on my configured key" when no key is configured.
    ///
    /// <para>Refused rather than quietly falling back to <c>/id</c>: that fallback IS
    /// <see cref="CosmosContainerCreationMode.UseDefaultPartitionKey"/>, and choosing it on the user's behalf
    /// would commit them to an irreversible layout they did not pick — precisely what selecting the configured
    /// mode says they wanted to avoid. The error names the fix.</para>
    /// </summary>
    private static CosmosContainerCreationMode ParseContainerCreationMode(
        string? raw, string? partitionKeyPath, string destinationName)
    {
        var mode = Enum.TryParse<CosmosContainerCreationMode>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : CosmosContainerCreationMode.Never;

        if (mode == CosmosContainerCreationMode.UseConfiguredPartitionKey
            && string.IsNullOrWhiteSpace(partitionKeyPath))
        {
            throw new InvalidOperationException(
                $"Destination '{destinationName}' is set to create missing Cosmos DB containers using its "
                    + "configured partition key, but no partition key path is set. Set one (e.g. '/id'), or "
                    + "switch container creation off.");
        }

        return mode;
    }

    /// <summary>
    /// A partition key path is a JSON pointer beginning with '/', e.g. <c>/resourceType</c>. A user typing the
    /// field name alone is the obvious mistake, and Cosmos rejects it with a message about the path rather than
    /// about the missing slash, so it is accepted and corrected here.
    /// </summary>
    internal static string? NormalizePartitionKeyPath(string? configured)
    {
        var path = (configured ?? string.Empty).Trim();

        return path.Length == 0 ? null : path.StartsWith('/') ? path : '/' + path;
    }

    private static FabricAuthMode ParseAuthMode(string? raw)
        => Enum.TryParse<FabricAuthMode>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : FabricAuthMode.ManagedIdentity;

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Require(string? value, string label, string destinationName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Destination '{destinationName}' is missing its Cosmos DB {label}.");
        }
    }
}
