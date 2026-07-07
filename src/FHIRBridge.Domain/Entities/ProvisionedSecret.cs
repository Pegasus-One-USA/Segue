namespace FHIRBridge.Domain.Entities;

/// <summary>
/// A secret value provisioned through the application and stored encrypted-at-rest (DataProtection). Identified by the
/// same (KeyVaultName, SecretName) reference an entity carries, so it is resolved exactly like a config/Key Vault
/// secret. Only the protected (encrypted) value is persisted — never the plaintext.
/// </summary>
public sealed class ProvisionedSecret
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
    public DateTime ModifiedOnUtc { get; private set; }

    public void Update(string protectedValue)
    {
        ProtectedValue = protectedValue;
        ModifiedOnUtc = DateTime.UtcNow;
    }
}
