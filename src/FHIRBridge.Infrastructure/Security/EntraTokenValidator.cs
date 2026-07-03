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
/// Validates a Microsoft Entra ID JWT: signature against the tenant's JWKS (discovered via the
/// OpenID Connect metadata document and cached), plus audience. Extracts the stable subject (oid/sub),
/// email/preferred_username, and display name. Only used by the SSO token-exchange endpoints.
/// </summary>
public sealed class EntraTokenValidator : IProviderTokenValidator
{
    private readonly EntraAuthenticationOptions _options;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
    private readonly JwtSecurityTokenHandler _handler = new();

    public EntraTokenValidator(IOptions<EntraAuthenticationOptions> options)
    {
        _options = options.Value;

        var authority = BuildAuthority(_options);
        var metadataAddress = $"{authority.TrimEnd('/')}/.well-known/openid-configuration";
        _configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = true });
    }

    public LoginProvider Provider => LoginProvider.Entra;

    public async Task<ExternalIdentity> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("An Entra token is required.");
        }

        var config = await _configurationManager.GetConfigurationAsync(cancellationToken);

        var validAudiences = new List<string>();
        if (!string.IsNullOrWhiteSpace(_options.Audience))
        {
            validAudiences.Add(_options.Audience);
            validAudiences.Add($"api://{_options.Audience}");
        }

        if (!string.IsNullOrWhiteSpace(_options.ClientId))
        {
            validAudiences.Add(_options.ClientId);
            validAudiences.Add($"api://{_options.ClientId}");
        }

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = config.Issuer,
            ValidateAudience = validAudiences.Count > 0,
            ValidAudiences = validAudiences,
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
            throw new InvalidOperationException("The Entra token is invalid or expired.", ex);
        }

        var subject = principal.FindFirst("oid")?.Value
            ?? principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("The Entra token does not contain a subject claim.");

        var email = principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value
            ?? principal.FindFirst("preferred_username")?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value
            ?? principal.FindFirst(ClaimTypes.Upn)?.Value
            ?? string.Empty;

        var name = principal.FindFirst("name")?.Value
            ?? principal.FindFirst(ClaimTypes.Name)?.Value;

        return new ExternalIdentity(LoginProvider.Entra, subject, email, name);
    }

    private static string BuildAuthority(EntraAuthenticationOptions options)
    {
        var instance = string.IsNullOrWhiteSpace(options.Instance)
            ? "https://login.microsoftonline.com/"
            : options.Instance;
        var tenant = string.IsNullOrWhiteSpace(options.TenantId) ? "common" : options.TenantId;

        return $"{instance.TrimEnd('/')}/{tenant}/v2.0";
    }
}
