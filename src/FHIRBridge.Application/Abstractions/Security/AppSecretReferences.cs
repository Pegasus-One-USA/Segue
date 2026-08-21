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

    /// <summary>HMAC-SHA256 key backing the Hashing/Masking transform node's "hash" mode — one key per
    /// install, auto-generated on first boot like the other app secrets, never persisted in a
    /// <see cref="Domain.Entities.TransformationRule.ConfigJson"/>.</summary>
    public static readonly SecretReference TransformHashingKey = new("app", "transform-hashing-key");
}
