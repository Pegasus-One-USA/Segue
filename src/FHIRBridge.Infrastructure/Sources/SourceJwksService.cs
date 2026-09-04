using System.Security.Cryptography;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Sources;

/// <summary>
/// Builds a public JWKS for a source connection from its stored SMART Backend Services private key. The private key PEM
/// is resolved via <see cref="ISecretProvider"/> purely to export its public RSA parameters; only the modulus and
/// exponent are published. The advertised <c>alg</c>/<c>kid</c> mirror what <c>BackendServicesJwtFactory</c> signs with
/// (RS384, and the connection's configured key id) so the EHR's key lookup matches the client assertion header.
/// </summary>
public sealed class SourceJwksService : ISourceJwksService
{
    // Matches the signing algorithm used in BackendServicesJwtFactory.CreateClientAssertion.
    private const string SigningAlgorithm = "RS384";

    private readonly IConfigurationRepository _configurationRepository;
    private readonly ISecretProvider _secretProvider;
    private readonly ILogger<SourceJwksService> _logger;

    public SourceJwksService(
        IConfigurationRepository configurationRepository,
        ISecretProvider secretProvider,
        ILogger<SourceJwksService> logger)
    {
        _configurationRepository = configurationRepository;
        _secretProvider = secretProvider;
        _logger = logger;
    }

    public async Task<JsonWebKeySetDto> GetPublicJwksAsync(
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        var sourceConnection = await _configurationRepository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken)
            ?? throw new NotFoundException("SourceConnection", sourceConnectionId);

        var privateKeyReference = sourceConnection.Authentication.PrivateKey;
        if (privateKeyReference is null)
        {
            // Symmetric (or no) client authentication — there is no public key to publish. An empty set is a valid
            // JWKS and lets the endpoint answer uniformly without leaking whether a key was expected.
            _logger.LogInformation(
                "Source {SourceConnectionId} has no asymmetric signing key; returning an empty JWKS.",
                sourceConnectionId);

            return new JsonWebKeySetDto([]);
        }

        var privateKeyPem = await _secretProvider.GetSecretAsync(privateKeyReference, cancellationToken);

        // The connection stores only a REFERENCE to the key, so a connection can be saved (and its JWKS URL handed
        // to the EHR) while the slot it points at was never populated — e.g. a hand-entered vault/secret name whose
        // value only exists as an appsettings placeholder. Diagnose that here, naming the connection and the exact
        // (vault, secret), the same way SourceConnectionRuntimeResolver does before signing an assertion.
        SigningKeySecretGuard.EnsurePemShaped(privateKeyPem, privateKeyReference, sourceConnection.Name);

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(privateKeyPem);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            // PEM-shaped (EnsurePemShaped passed) but not importable — a truncated block, wrong PEM label, or a
            // non-RSA key. Says nothing about the value: it is key material whenever it is valid.
            throw new InvalidOperationException(
                $"The signing key for source connection '{sourceConnection.Name}' could not be read as an RSA " +
                $"private key. Secret '{privateKeyReference.SecretName}' in '{privateKeyReference.KeyVaultName}' " +
                "contains a PEM block that is truncated, encrypted, or not an RSA private key. Re-provision it via " +
                "the Generate/Import signing-key source, then register this JWKS URL with the EHR again.",
                exception);
        }

        // ImportFromPem accepts a public-key or certificate PEM without error, and the public parameters below
        // would then export a JWKS that looks perfectly valid — while FHIRBridge holds no private key to actually
        // sign assertions with, so the EHR only discovers the problem at token time. SigningKeyGenerationService
        // rejects a public key on import for the same reason; this catches one populated into the reference by
        // hand. Only a key with its private component present can export one.
        try
        {
            rsa.ExportParameters(includePrivateParameters: true);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException(
                $"The signing key for source connection '{sourceConnection.Name}' is a public key or certificate, " +
                $"not a private key. Secret '{privateKeyReference.SecretName}' in " +
                $"'{privateKeyReference.KeyVaultName}' must hold the RSA PRIVATE key FHIRBridge signs assertions " +
                "with — this endpoint derives the public half to publish. Re-provision it via the Generate/Import " +
                "signing-key source.",
                exception);
        }

        var parameters = rsa.ExportParameters(includePrivateParameters: false);

        var key = new JsonWebKeyDto(
            KeyType: "RSA",
            Use: "sig",
            Algorithm: SigningAlgorithm,
            KeyId: string.IsNullOrWhiteSpace(sourceConnection.Authentication.KeyId)
                ? null
                : sourceConnection.Authentication.KeyId,
            Modulus: Base64UrlEncode(parameters.Modulus!),
            Exponent: Base64UrlEncode(parameters.Exponent!));

        return new JsonWebKeySetDto([key]);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
