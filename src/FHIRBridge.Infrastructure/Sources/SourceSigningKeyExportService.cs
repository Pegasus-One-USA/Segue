using System.Security.Cryptography;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Sources;

/// <summary>
/// Reads a source connection's stored SMART Backend Services private key from the secret store and re-exports it as
/// PEM — either its public half (for an EHR app registration that takes an uploaded public key rather than a JWKS
/// URL) or the private key itself (for a customer keeping a copy of a key FHIRBridge generated for them). Mirrors
/// <see cref="SourceJwksService"/>'s resolution path; the private-key export is the one place in the codebase that
/// deliberately hands secret material back out, and its caller is responsible for permission-gating and auditing it.
/// </summary>
public sealed class SourceSigningKeyExportService : ISourceSigningKeyExportService
{
    private readonly IConfigurationRepository _configurationRepository;
    private readonly ISecretProvider _secretProvider;
    private readonly ILogger<SourceSigningKeyExportService> _logger;

    public SourceSigningKeyExportService(
        IConfigurationRepository configurationRepository,
        ISecretProvider secretProvider,
        ILogger<SourceSigningKeyExportService> logger)
    {
        _configurationRepository = configurationRepository;
        _secretProvider = secretProvider;
        _logger = logger;
    }

    public async Task<string?> GetPublicKeyPemAsync(Guid sourceConnectionId, CancellationToken cancellationToken)
    {
        var privateKeyPem = await ResolvePrivateKeyPemAsync(sourceConnectionId, cancellationToken);
        if (privateKeyPem is null)
        {
            return null;
        }

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);

        return rsa.ExportSubjectPublicKeyInfoPem();
    }

    public async Task<string?> GetPrivateKeyPemAsync(Guid sourceConnectionId, CancellationToken cancellationToken)
    {
        var privateKeyPem = await ResolvePrivateKeyPemAsync(sourceConnectionId, cancellationToken);
        if (privateKeyPem is null)
        {
            return null;
        }

        // Normalized to PKCS#8 so an imported PKCS#1 key comes back in the same shape a generated one does — the
        // same normalization SigningKeyGenerationService applies on the way in.
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);

        return rsa.ExportPkcs8PrivateKeyPem();
    }

    private async Task<string?> ResolvePrivateKeyPemAsync(Guid sourceConnectionId, CancellationToken cancellationToken)
    {
        var sourceConnection = await _configurationRepository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken)
            ?? throw new NotFoundException("SourceConnection", sourceConnectionId);

        var privateKeyReference = sourceConnection.Authentication.PrivateKey;
        if (privateKeyReference is null)
        {
            _logger.LogInformation(
                "Source {SourceConnectionId} has no asymmetric signing key; nothing to export.",
                sourceConnectionId);

            return null;
        }

        return await _secretProvider.GetSecretAsync(privateKeyReference, cancellationToken);
    }
}
