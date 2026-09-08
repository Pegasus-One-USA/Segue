using FHIRBridge.Application.Abstractions.Security;

namespace FHIRBridge.UnitTests.Security;

/// <summary>
/// Test double for <see cref="ITenantSecretVaultResolver"/> that always returns the caller-supplied vault name
/// unchanged (no Key Vault configuration to resolve against in these unit tests). Used by tests that construct
/// services depending on this resolver directly and don't care about actual vault-name resolution.
/// </summary>
public sealed class PassthroughTenantSecretVaultResolver : ITenantSecretVaultResolver
{
    public string ResolveVaultName(string requestedKeyVaultName) => requestedKeyVaultName;
}
