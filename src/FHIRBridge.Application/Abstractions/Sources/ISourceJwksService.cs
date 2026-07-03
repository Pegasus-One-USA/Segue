using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Sources;

/// <summary>
/// Publishes the public half of a source connection's SMART Backend Services signing key as a JSON Web Key Set, so a
/// customer can register the connection's <c>.well-known/jwks.json</c> URL with their EHR and the authorization server
/// can verify FHIRBridge's <c>private_key_jwt</c> client assertions. The private key is resolved from the secret store
/// only to derive its public parameters — it is never exposed.
/// </summary>
public interface ISourceJwksService
{
    /// <summary>
    /// Returns the public JWKS for the source connection. A connection with no asymmetric signing key configured yields
    /// an empty key set (a valid JWKS); a missing source connection throws.
    /// </summary>
    Task<JsonWebKeySetDto> GetPublicJwksAsync(
        Guid sourceConnectionId,
        CancellationToken cancellationToken);
}
