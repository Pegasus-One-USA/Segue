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

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
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
