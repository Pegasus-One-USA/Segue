using FHIRBridge.Application.Services;
using System.Text.Json;
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

    /// <summary>Accepts the request the way OAuth 2.0 defines it (form-encoded <c>grant_type</c>, <c>client_id</c>,
    /// <c>client_secret</c>) as well as JSON with either snake_case (<c>grant_type</c>) or camelCase
    /// (<c>grantType</c>) names - callers use all three, and a mismatch used to surface only as a 400 about missing fields.</summary>
    [HttpPost("token")]
    [Consumes("application/json", "application/x-www-form-urlencoded")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> IssueToken(CancellationToken cancellationToken)
    {
        var request = await ReadRequestAsync(cancellationToken);
        if (request is null)
        {
            return BadRequest(new { error = "invalid_request" });
        }

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

    private async Task<ClientCredentialsTokenRequest?> ReadRequestAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (Request.HasFormContentType)
            {
                var form = await Request.ReadFormAsync(cancellationToken);
                return new ClientCredentialsTokenRequest(form["grant_type"].ToString(), form["client_id"].ToString(), form["client_secret"].ToString());
            }

            using var document = await JsonDocument.ParseAsync(Request.Body, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // "grant_type", "grantType" and "GrantType" all normalise to "granttype".
            static string Normalise(string name) => name.Replace("_", string.Empty).ToLowerInvariant();
            var values = document.RootElement.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.String)
                .GroupBy(property => Normalise(property.Name))
                .ToDictionary(group => group.Key, group => group.First().Value.GetString() ?? string.Empty);
            string Get(string key) => values.GetValueOrDefault(key, string.Empty);

            return new ClientCredentialsTokenRequest(Get("granttype"), Get("clientid"), Get("clientsecret"));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or BadHttpRequestException)
        {
            return null;
        }
    }
}
