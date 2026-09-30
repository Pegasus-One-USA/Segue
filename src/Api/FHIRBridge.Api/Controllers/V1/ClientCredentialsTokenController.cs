using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FHIRBridge.Api.Controllers.V1;

public sealed record ClientCredentialsTokenRequest(string GrantType, string ClientId, string ClientSecret);

/// <summary>
/// OAuth 2.0 Client Credentials Grant token endpoint (RFC 6749 §4.4) — the machine-to-machine counterpart of
/// the portal's user login. A third-party application exchanges the Client ID/Secret an admin generated under
/// Settings &gt; API Clients (<c>ApiClientsController</c>) for a short-lived Bearer token, then sends that
/// token to <c>POST /workflows/{id}/run</c> to trigger any workflow. Anonymous by design — the credentials
/// themselves are the authentication — and rate-limited like every other OAuth-shaped endpoint in this API.
/// </summary>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting("oauth")]
[Route("api/v1/oauth")]
public sealed class ClientCredentialsTokenController : ControllerBase
{
    private readonly IClientCredentialsTokenService _tokenService;

    public ClientCredentialsTokenController(IClientCredentialsTokenService tokenService)
    {
        _tokenService = tokenService;
    }

    [HttpPost("token")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> IssueToken(
        [FromBody] ClientCredentialsTokenRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.GrantType, "client_credentials", StringComparison.Ordinal))
        {
            return BadRequest(new { error = "unsupported_grant_type" });
        }

        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.ClientSecret))
        {
            return BadRequest(new { error = "invalid_request" });
        }

        var result = await _tokenService.IssueTokenAsync(request.ClientId, request.ClientSecret, cancellationToken);
        if (!result.Success)
        {
            // Same generic response whether the client id is unknown, disabled, or the secret is wrong —
            // see ClientCredentialsTokenService's remarks on why these are never distinguished to the caller.
            return Unauthorized(new { error = "invalid_client" });
        }

        return Ok(new
        {
            access_token = result.Token!.AccessToken,
            token_type = result.Token.TokenType,
            expires_in = (int)(result.Token.ExpiresOnUtc - DateTime.UtcNow).TotalSeconds,
        });
    }
}
