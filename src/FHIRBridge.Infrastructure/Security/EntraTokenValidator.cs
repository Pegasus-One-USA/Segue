using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FHIRBridge.Application.Abstractions.Caching;
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
/// Instance/TenantId/ClientId/Audience are resolved live via <see cref="ISystemSettingsCache"/> (SSO
/// Configurations admin screen) on every call, falling back to the appsettings value — same live-read
/// pattern as SamlConfigurationProvider, so a change saved from the screen takes effect on the very
/// next sign-in attempt, no restart. (This covers the "Continue with Microsoft" login path only — the
/// separate JWT bearer scheme in FhirBridgeAuthenticationExtensions, used if a caller presents a raw
/// Entra token directly as an API Authorization header, is still fixed at startup regardless.)
/// </summary>
public sealed class EntraTokenValidator : IProviderTokenValidator
{
    private const string InstanceKey = "Authentication:Entra:Instance";
    private const string TenantIdKey = "Authentication:Entra:TenantId";
    private const string ClientIdKey = "Authentication:Entra:ClientId";
    private const string AudienceKey = "Authentication:Entra:Audience";

    private readonly EntraAuthenticationOptions _fallback;
    private readonly ISystemSettingsCache _settingsCache;
    private readonly JwtSecurityTokenHandler _handler = new();

    // Keyed by authority — rebuilding a ConfigurationManager on every call would re-fetch/re-cache JWKS
    // needlessly; this only grows a new entry when TenantId/Instance actually changes.
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _configManagers = new();

    public EntraTokenValidator(IOptions<EntraAuthenticationOptions> options, ISystemSettingsCache settingsCache)
    {
        _fallback = options.Value;
        _settingsCache = settingsCache;
    }

    public LoginProvider Provider => LoginProvider.Entra;

    public async Task<ExternalIdentity> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("An Entra token is required.");
        }

        var instance = await _settingsCache.GetStringAsync(
            InstanceKey, _fallback.Instance ?? "https://login.microsoftonline.com/", cancellationToken);
        var tenantId = await _settingsCache.GetStringAsync(TenantIdKey, _fallback.TenantId ?? string.Empty, cancellationToken);
        var clientId = await _settingsCache.GetStringAsync(ClientIdKey, _fallback.ClientId ?? string.Empty, cancellationToken);
        var audience = await _settingsCache.GetStringAsync(AudienceKey, _fallback.Audience ?? string.Empty, cancellationToken);

        var authority = BuildAuthority(instance, tenantId);
        var configurationManager = GetOrAddConfigurationManager(authority);
        var config = await configurationManager.GetConfigurationAsync(cancellationToken);

        var validAudiences = new List<string>();
        if (!string.IsNullOrWhiteSpace(audience))
        {
            validAudiences.Add(audience);
            validAudiences.Add($"api://{audience}");
        }

        if (!string.IsNullOrWhiteSpace(clientId))
        {
            validAudiences.Add(clientId);
            validAudiences.Add($"api://{clientId}");
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
            ?? throw new InvalidOperationException("Sign-in failed. Please try again.");

        var email = principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value
            ?? principal.FindFirst("preferred_username")?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value
            ?? principal.FindFirst(ClaimTypes.Upn)?.Value
            ?? string.Empty;

        var name = principal.FindFirst("name")?.Value
            ?? principal.FindFirst(ClaimTypes.Name)?.Value;

        return new ExternalIdentity(LoginProvider.Entra, subject, email, name);
    }

    private ConfigurationManager<OpenIdConnectConfiguration> GetOrAddConfigurationManager(string authority) =>
        _configManagers.GetOrAdd(authority, a =>
        {
            var metadataAddress = $"{a.TrimEnd('/')}/.well-known/openid-configuration";
            return new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = true });
        });

    private static string BuildAuthority(string instance, string tenantId)
    {
        var resolvedInstance = string.IsNullOrWhiteSpace(instance) ? "https://login.microsoftonline.com/" : instance;
        var tenant = string.IsNullOrWhiteSpace(tenantId) ? "common" : tenantId;

        return $"{resolvedInstance.TrimEnd('/')}/{tenant}/v2.0";
    }
}
