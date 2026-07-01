namespace FHIRBridge.Domain.ValueObjects;

public sealed record SecretReference(
    string KeyVaultName,
    string SecretName);
