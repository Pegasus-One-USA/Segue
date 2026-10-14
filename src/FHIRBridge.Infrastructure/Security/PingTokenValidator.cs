using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Enums;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// Validates a PingOne OIDC ID token: signature against the environment's JWKS (discovered through the
/// OpenID Connect metadata document and cached), issuer, and audience = the portal application's client id.
/// The subject (<c>sub</c>) is the PingOne user id, which <see cref="PingRoleResolver"/> uses to look up the
/// user's group membership. Only used by the SSO token-exchange endpoint.
/// </summary>
public sealed class PingTokenValidator : IProviderTokenValidator
{
    private readonly PingSettingsReader _settings;
    private readonly JwtSecurityTokenHandler _handler = new();
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _configManagers = new();

    public PingTokenValidator(PingSettingsReader settings)
    {
        _settings = settings;
    }

    public LoginProvider Provider => LoginProvider.Ping;

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken) =>
        (await _settings.GetAsync(cancellationToken)).Enabled;

    public async Task<ExternalIdentity> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("A Ping token is required.");
        }

        var settings = await _settings.GetAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.EnvironmentId) || string.IsNullOrWhiteSpace(settings.ClientId))
        {
            throw new InvalidOperationException("Ping sign-in is not configured.");
        }

        var configurationManager = _configManagers.GetOrAdd(settings.Authority, authority =>
            new ConfigurationManager<OpenIdConnectConfiguration>(
                $"{authority}/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = true }));
        var config = await configurationManager.GetConfigurationAsync(cancellationToken);

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = config.Issuer,
            ValidateAudience = true,
            ValidAudience = settings.ClientId,
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
            throw new InvalidOperationException("The Ping token is invalid or expired.", ex);
        }

        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("Sign-in failed. Please try again.");

        var email = principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value
            ?? string.Empty;

        var name = principal.FindFirst("name")?.Value
            ?? principal.FindFirst(ClaimTypes.Name)?.Value;

        return new ExternalIdentity(LoginProvider.Ping, subject, email, name);
    }
}
