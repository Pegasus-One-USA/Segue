using System.Security.Cryptography;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Sources;

/// <summary>
/// Provisions RSA signing keys for SMART Backend Services — either generating a fresh pair or validating and
/// storing a customer-supplied one — and writes the private key (PKCS8 PEM) to the secret store via
/// <see cref="ISecretWriter"/>. Either way, only a reference to it is ever returned; the private key itself never
/// leaves this class. Every provisioned key gets its own secret name (never reused/overwritten), so
/// re-generating/re-importing from the wizard can't invalidate a key an earlier save already wired into a
/// connection.
/// </summary>
public sealed class SigningKeyGenerationService : ISigningKeyGenerationService
{
    // Matches the signing algorithm SourceJwksService advertises and BackendServicesJwtFactory signs with. Fixed
    // for both flows: the key TYPE (RSA) determines the algorithm family, not how the key was provisioned, and
    // BackendServicesJwtFactory only ever signs RS384 regardless of the imported key's original intended use.
    private const string SigningAlgorithm = "RS384";
    private const int GeneratedKeySizeBits = 2048;

    // The minimum accepted for an imported key — anything smaller is rejected as too weak to trust for a
    // customer-facing EHR integration, even though the customer (not this service) chose the key size.
    private const int MinimumImportedKeySizeBits = 2048;

    // Not a customer-provisioned Key Vault — a fixed bucket for signing keys this instance holds on a customer's
    // behalf (whether generated here or imported from the customer), resolved through the same
    // ISecretProvider/ISecretWriter path (DbSecretStore in dev, Key Vault in prod) as every other secret reference.
    private const string SigningKeyVaultName = "signing-keys";

    private readonly ISecretWriter _secretWriter;
    private readonly ILogger<SigningKeyGenerationService> _logger;

    public SigningKeyGenerationService(ISecretWriter secretWriter, ILogger<SigningKeyGenerationService> logger)
    {
        _secretWriter = secretWriter;
        _logger = logger;
    }

    public async Task<GeneratedSigningKeyDto> GenerateAsync(CancellationToken cancellationToken)
    {
        using var rsa = RSA.Create(GeneratedKeySizeBits);
        var privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();

        var result = await StoreAsync(privateKeyPem, cancellationToken);

        _logger.LogInformation(
            "Generated a new {Algorithm}/{KeySizeBits}-bit signing key pair (kid {KeyId}); private key stored as " +
            "'{SecretName}' in vault '{KeyVaultName}'.",
            SigningAlgorithm,
            GeneratedKeySizeBits,
            result.KeyId,
            result.SecretName,
            result.KeyVaultName);

        return result;
    }

    public async Task<GeneratedSigningKeyDto> ImportAsync(string privateKeyPem, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPem))
        {
            throw new BusinessRuleException("A private key is required to import.");
        }

        var normalizedPem = ValidateAndNormalize(privateKeyPem);

        var result = await StoreAsync(normalizedPem, cancellationToken);

        _logger.LogInformation(
            "Imported a customer-supplied {Algorithm} signing key (kid {KeyId}); private key stored as " +
            "'{SecretName}' in vault '{KeyVaultName}'.",
            SigningAlgorithm,
            result.KeyId,
            result.SecretName,
            result.KeyVaultName);

        return result;
    }

    /// <summary>
    /// Confirms <paramref name="privateKeyPem"/> imports as a real RSA private key with an extractable private
    /// component (rejects a public key or certificate PEM), an unencrypted PEM (rejects "ENCRYPTED PRIVATE KEY" —
    /// there's no passphrase field in this flow to decrypt one), and a key size at least
    /// <see cref="MinimumImportedKeySizeBits"/>. Returns the key re-exported as PKCS8 PEM so every stored signing
    /// key — generated or imported — has the exact same on-disk shape, regardless of whether the customer's
    /// original file was PKCS1 ("BEGIN RSA PRIVATE KEY") or PKCS8 ("BEGIN PRIVATE KEY").
    /// </summary>
    private static string ValidateAndNormalize(string privateKeyPem)
    {
        using var rsa = RSA.Create();

        try
        {
            rsa.ImportFromPem(privateKeyPem);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        {
            throw new BusinessRuleException(
                "The provided file is not a valid, unencrypted RSA private key in PEM format. Supported formats " +
                "are PKCS#1 (\"BEGIN RSA PRIVATE KEY\") and PKCS#8 (\"BEGIN PRIVATE KEY\").");
        }

        string normalizedPem;
        try
        {
            // Only a key with its private component present can export one — this is what actually rejects a
            // public-key or certificate PEM (ImportFromPem accepts those without error, since they're valid PEMs,
            // just not private keys).
            normalizedPem = rsa.ExportPkcs8PrivateKeyPem();
        }
        catch (CryptographicException)
        {
            throw new BusinessRuleException(
                "The provided file is a public key or certificate, not a private key. Import the RSA private key " +
                "instead.");
        }

        if (rsa.KeySize < MinimumImportedKeySizeBits)
        {
            throw new BusinessRuleException(
                $"The imported RSA key is {rsa.KeySize}-bit, below the minimum required {MinimumImportedKeySizeBits}-bit. " +
                "Use a stronger key.");
        }

        return normalizedPem;
    }

    private async Task<GeneratedSigningKeyDto> StoreAsync(string privateKeyPem, CancellationToken cancellationToken)
    {
        var keyId = $"fb-{Guid.NewGuid():N}"[..12];
        var secretName = $"epic-private-key-{Guid.NewGuid():N}";
        var secretReference = new SecretReference(SigningKeyVaultName, secretName);

        await _secretWriter.WriteSecretAsync(secretReference, privateKeyPem, cancellationToken);

        return new GeneratedSigningKeyDto(keyId, SigningKeyVaultName, secretName, SigningAlgorithm);
    }
}
