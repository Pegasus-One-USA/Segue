using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Publishes the public JWKS for a source connection's SMART Backend Services signing key. A customer registers this
/// URL with their EHR so the authorization server can fetch FHIRBridge's public key and verify the
/// <c>private_key_jwt</c> client assertions it signs. Anonymous by design — the response contains only public key
/// material and the EHR fetches it server-to-server without a FHIRBridge session.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/source-connections/{sourceConnectionId:guid}")]
public sealed class SourceJwksController : ControllerBase
{
    private readonly ISourceJwksService _jwksService;

    public SourceJwksController(ISourceJwksService jwksService)
    {
        _jwksService = jwksService;
    }

    /// <summary>
    /// Returns the source connection's public keys as a JWKS. A connection with no asymmetric signing key returns an
    /// empty (but valid) key set; an unknown source connection returns 404.
    /// </summary>
    [HttpGet(".well-known/jwks.json")]
    [ProducesResponseType(typeof(JsonWebKeySetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJwks(
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        var jwks = await _jwksService.GetPublicJwksAsync(sourceConnectionId, cancellationToken);

        return Ok(jwks);
    }
}
