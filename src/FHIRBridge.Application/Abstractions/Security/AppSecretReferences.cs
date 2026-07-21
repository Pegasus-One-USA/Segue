using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// Stable (KeyVaultName, SecretName) references for the app-level secrets exposed via
/// <see cref="IAppSecretAccessor"/> — distinct from tenant/connection secrets, which carry their own
/// <see cref="SecretReference"/> per entity.
/// </summary>
public static class AppSecretReferences
{
    public static readonly SecretReference JwtSigningKey = new("app", "jwt-signing-key");

    public static readonly SecretReference DownloadLinkSigningSecret = new("app", "download-link-signing-secret");
}
