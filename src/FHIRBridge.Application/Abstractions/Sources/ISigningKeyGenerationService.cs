using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Sources;

/// <summary>
/// Provisions asymmetric signing keys for SMART Backend Services (<c>private_key_jwt</c>) authentication and
/// persists the private key to the secret store. Backs the source-connection wizard's two mutually exclusive
/// "Segue-generated" options: generate a brand-new key pair, or import a customer-supplied one for an Epic app
/// they've already registered elsewhere. Either way the wizard carries the returned (Key Vault name, secret name,
/// key id) into the connection's Authentication config on save, exactly as it would for an external key reference.
/// </summary>
public interface ISigningKeyGenerationService
{
    /// <summary>Generates a brand-new RSA key pair and stores the private key.</summary>
    Task<GeneratedSigningKeyDto> GenerateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Validates <paramref name="privateKeyPem"/> as a real, unencrypted RSA private key — rejecting a public key, a
    /// certificate, a non-RSA key, or an encrypted PEM with a <c>BusinessRuleException</c> — and stores it as-is; no
    /// new key material is generated. Used when a customer already has an Epic app registered against their own key
    /// and wants FHIRBridge to sign with that same key instead of a new one.
    /// </summary>
    Task<GeneratedSigningKeyDto> ImportAsync(string privateKeyPem, CancellationToken cancellationToken);
}
