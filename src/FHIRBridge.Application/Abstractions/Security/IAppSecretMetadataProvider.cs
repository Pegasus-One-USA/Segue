using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Application.Abstractions.Security;

public sealed record ProvisionedSecretMetadata(bool Provisioned, DateTime? LastRotatedUtc);

/// <summary>
/// Read-only metadata about a provisioned app secret — whether it exists and when it was last written —
/// without ever exposing or decrypting the value itself. Backed by the same store as
/// <see cref="ISecretProvider"/>/<see cref="ISecretWriter"/> (DbSecretStore in production, InMemorySecretStore
/// when no database is configured).
/// </summary>
public interface IAppSecretMetadataProvider
{
    Task<ProvisionedSecretMetadata> GetMetadataAsync(SecretReference secretReference, CancellationToken cancellationToken);
}
