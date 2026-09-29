using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// A secret value provisioned through the application and stored encrypted-at-rest (DataProtection). Identified by the
/// same (KeyVaultName, SecretName) reference an entity carries, so it is resolved exactly like a config/Key Vault
/// secret. Only the protected (encrypted) value is persisted — never the plaintext.
/// </summary>
public sealed class ProvisionedSecret : IAuditableEntity
{
    public ProvisionedSecret(string keyVaultName, string secretName, string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(keyVaultName))
        {
            throw new ArgumentException("Key vault name is required.", nameof(keyVaultName));
        }

        if (string.IsNullOrWhiteSpace(secretName))
        {
            throw new ArgumentException("Secret name is required.", nameof(secretName));
        }

        Id = Guid.NewGuid();
        KeyVaultName = keyVaultName.Trim();
        SecretName = secretName.Trim();
        ProtectedValue = protectedValue;
        CreatedOnUtc = DateTime.UtcNow;
        ModifiedOnUtc = DateTime.UtcNow;
    }

    private ProvisionedSecret() { } // EF

    public Guid Id { get; private set; }
    public string KeyVaultName { get; private set; } = default!;
    public string SecretName { get; private set; } = default!;
    public string ProtectedValue { get; private set; } = default!;
    public DateTime CreatedOnUtc { get; private set; }

    /// <summary>Nullable to satisfy <see cref="IAuditableEntity"/>. Still set on construction (unlike
    /// <c>AuditableChildEntity</c>, where it stays null until a real update) so existing rows and readers keep
    /// seeing a value.</summary>
    public DateTime? ModifiedOnUtc { get; private set; }

    /// <summary>
    /// Seeded rather than left null for the interceptor to fill, because <c>CreatedBy</c> is mapped NOT NULL for
    /// every <see cref="IAuditableEntity"/> (see FHIRBridgeDbContext) and a secret can legitimately be written
    /// outside a user request — a startup seed or a background provisioning path has no current user, and the
    /// database default only covers providers that honour it. <c>ApplyCreated</c> overwrites this with the real
    /// actor whenever there is one.
    /// </summary>
    public string? CreatedBy { get; private set; } = "system";

    public string? ModifiedBy { get; private set; }

    public void Update(string protectedValue)
    {
        ProtectedValue = protectedValue;
        ModifiedOnUtc = DateTime.UtcNow;
    }

    public void ApplyCreated(string? userId, DateTime utcNow)
    {
        CreatedOnUtc = utcNow;
        CreatedBy = userId;
        ModifiedOnUtc ??= utcNow;
    }

    public void ApplyModified(string? userId, DateTime utcNow)
    {
        ModifiedOnUtc = utcNow;
        ModifiedBy = userId;
    }
}
