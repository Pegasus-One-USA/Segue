namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// Decrypts a raw <c>ProvisionedSecrets.ProtectedValue</c> blob back to its plaintext (e.g. a private key PEM) —
/// a SuperAdmin recovery tool for when the owning <c>SourceConnection</c> row is gone, unreachable, or on a
/// different instance whose UI can't be used directly. Only ever works for a value encrypted by <em>this same
/// instance's</em> Data Protection key ring (see DbSecretStore) — a ProtectedValue copied from a different
/// FHIRBridge deployment will fail to decrypt here, since key rings are per-instance/per-deployment, not shared,
/// unless that deployment was explicitly configured with a shared key ring path.
/// </summary>
public interface IProvisionedSecretDecryptor
{
    /// <summary>Throws <see cref="System.Security.Cryptography.CryptographicException"/> (surfaced as a 400 by
    /// the controller) when <paramref name="protectedValue"/> wasn't encrypted by this instance's own key ring —
    /// e.g. it came from a different deployment, or isn't a real ProtectedValue at all.</summary>
    string Decrypt(string protectedValue);
}
