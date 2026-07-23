namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Result of generating a new SMART Backend Services signing key pair. The private key is written straight to the
/// secret store and never included here — only the reference needed to wire a <c>SourceConnection</c>'s
/// <c>Authentication.PrivateKey</c> (Key Vault name + secret name) and the key id the client assertion/JWKS will
/// carry.
/// </summary>
public sealed record GeneratedSigningKeyDto(
    string KeyId,
    string KeyVaultName,
    string SecretName,
    string Algorithm);
