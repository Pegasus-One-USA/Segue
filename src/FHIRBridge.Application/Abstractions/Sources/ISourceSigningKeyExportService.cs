namespace FHIRBridge.Application.Abstractions.Sources;

/// <summary>
/// Exports a source connection's SMART Backend Services signing key as PEM text, for a customer registering the
/// key with an EHR that takes an uploaded public key instead of a JWKS URL (and, separately, for keeping a copy of
/// a key FHIRBridge generated on their behalf). Distinct from <see cref="ISourceJwksService"/>, which only ever
/// publishes public parameters and is anonymous: the private-key half here is a deliberately privileged,
/// audit-logged operation, so the two are kept apart rather than folded into one service.
/// </summary>
public interface ISourceSigningKeyExportService
{
    /// <summary>
    /// The public half of the connection's signing key as a SubjectPublicKeyInfo PEM ("BEGIN PUBLIC KEY"), derived
    /// from the stored private key exactly as <see cref="ISourceJwksService"/> derives the JWKS. Null when the
    /// connection has no asymmetric signing key configured; throws when the connection doesn't exist.
    /// </summary>
    Task<string?> GetPublicKeyPemAsync(Guid sourceConnectionId, CancellationToken cancellationToken);

    /// <summary>
    /// The connection's stored private key as a PKCS#8 PEM ("BEGIN PRIVATE KEY") — real secret material. Callers
    /// must gate this behind its own permission and record who exported it. Null when the connection has no
    /// asymmetric signing key configured; throws when the connection doesn't exist.
    /// </summary>
    Task<string?> GetPrivateKeyPemAsync(Guid sourceConnectionId, CancellationToken cancellationToken);
}
