namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// Resolves the effective Key Vault name for a tenant-owned secret (a <c>SourceConnection</c>'s OAuth client
/// secret, or a <c>DestinationConfiguration</c>'s connection secret) being freshly provisioned via
/// <see cref="ISecretWriter"/>. When Azure Key Vault mode is off, the caller-supplied name is used as-is (today's
/// local-lookup-key behavior); when it's on, provisioning targets the one configured tenant-secrets vault instead
/// of whatever placeholder name the caller sent, so the <c>FHIRBridge.Domain.ValueObjects.SecretReference</c>
/// persisted on the entity is the real vault a later <c>ISecretProvider.GetSecretAsync</c> call can resolve.
/// </summary>
public interface ITenantSecretVaultResolver
{
    string ResolveVaultName(string requestedKeyVaultName);
}
