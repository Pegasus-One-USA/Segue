using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Enums;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// Validates a Google ID token (JWT): signature against Google's JWKS (discovered via Google's OIDC
/// metadata and cached), issuer <c>accounts.google.com</c> / <c>https://accounts.google.com</c>, and
/// audience = the configured client id. Extracts sub, email, and name. Only used by the SSO endpoints.
/// </summary>
public sealed class GoogleTokenValidator : IProviderTokenValidator
{
    private const string MetadataAddress = "https://accounts.google.com/.well-known/openid-configuration";

    private static readonly string[] ValidIssuers =
    [
        "https://accounts.google.com",
        "accounts.google.com"
    ];

    private readonly GoogleAuthenticationOptions _options;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
    private readonly JwtSecurityTokenHandler _handler = new();

    public GoogleTokenValidator(IOptions<GoogleAuthenticationOptions> options)
    {
        _options = options.Value;
        _configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            MetadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = true });
    }

    public LoginProvider Provider => LoginProvider.Google;

    public async Task<ExternalIdentity> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("A Google token is required.");
        }

        if (string.IsNullOrWhiteSpace(_options.ClientId))
        {
            throw new InvalidOperationException("Sign-in with Google isn't available right now.");
        }

        var config = await _configurationManager.GetConfigurationAsync(cancellationToken);

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = ValidIssuers,
            ValidateAudience = true,
            ValidAudience = _options.ClientId,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = config.SigningKeys,
            ValidateLifetime = true
        };

        ClaimsPrincipal principal;
        try
        {
            principal = _handler.ValidateToken(token, parameters, out _);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("The Google token is invalid or expired.", ex);
        }

        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("Sign-in failed. Please try again.");

        var email = principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value
            ?? string.Empty;

        var name = principal.FindFirst("name")?.Value
            ?? principal.FindFirst(ClaimTypes.Name)?.Value;

        return new ExternalIdentity(LoginProvider.Google, subject, email, name);
    }
}
