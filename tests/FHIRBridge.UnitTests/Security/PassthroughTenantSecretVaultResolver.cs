using FHIRBridge.Application.Abstractions.Security;

namespace FHIRBridge.UnitTests.Security;

/// <summary>
/// Test double for <see cref="ITenantSecretVaultResolver"/> that returns every requested vault name unchanged —
/// the same behavior as the real resolver when Azure Key Vault mode is off. Used by tests that construct
/// <c>ConfigurationService</c> directly and don't exercise Key Vault override behavior.
/// </summary>
public sealed class PassthroughTenantSecretVaultResolver : ITenantSecretVaultResolver
{
    public string ResolveVaultName(string requestedKeyVaultName) => requestedKeyVaultName;
}
