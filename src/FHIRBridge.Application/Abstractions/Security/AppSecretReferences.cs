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

    /// <summary>AES-256-GCM key backing <see cref="IPhiFieldEncryptor"/> (PHI-bearing execution-history
    /// columns) — one key per install, auto-generated on first boot like the other app secrets. Deliberately
    /// excluded from <c>AppSecretsAdminService</c>'s regeneration catalog: unlike the signing secrets,
    /// rotating this key would leave every previously-encrypted row undecryptable.</summary>
    public static readonly SecretReference PhiEncryptionKey = new("app", "phi-encryption-key");
}
