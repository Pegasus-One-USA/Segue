namespace FHIRBridge.Infrastructure.Destinations.Blob;

/// <summary>
/// The closed set of ways a tenant can authenticate to Azure Blob Storage. Client storage setups vary widely
/// (classic account key, least-privilege SAS, Azure-hosted with managed identity, cross-tenant service
/// principal), so the writer supports all five rather than assuming one shape.
/// </summary>
public enum BlobDestinationAuthMode
{
    ConnectionString = 0,
    AccountKey,
    SasUrl,
    ManagedIdentity,
    ServicePrincipal,
}
