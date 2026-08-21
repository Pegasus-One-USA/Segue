using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using Microsoft.IdentityModel.Tokens;

namespace FHIRBridge.Api.Security;

public sealed class JwtAccessTokenIssuer : IAccessTokenIssuer
{
    private readonly IConfiguration _configuration;
    private readonly IAppSecretAccessor _secretAccessor;
    private readonly ISystemSettingsCache _settingsCache;

    public JwtAccessTokenIssuer(
        IConfiguration configuration, IAppSecretAccessor secretAccessor, ISystemSettingsCache settingsCache)
    {
        _configuration = configuration;
        _secretAccessor = secretAccessor;
        _settingsCache = settingsCache;
    }

    public AccessTokenDto Issue(
        User user,
        IReadOnlyCollection<string> roleNames)
    {
        var signingKey = _secretAccessor.JwtSigningKey;
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            throw new InvalidOperationException("The JWT signing key has not been provisioned yet.");
        }

        var defaultMinutes = int.TryParse(_configuration["Authentication:TokenLifetimeMinutes"], out var minutes)
            ? minutes
            : 60;
        var tokenLifetimeMinutes = _settingsCache
            .GetIntAsync("Authentication:TokenLifetimeMinutes", defaultMinutes, default)
            .GetAwaiter().GetResult();
        var expiresOnUtc = DateTime.UtcNow.AddMinutes(tokenLifetimeMinutes);

        var claims = new List<Claim>
        {
            new("oid", user.ExternalUserId),
            new(ClaimTypes.NameIdentifier, user.ExternalUserId),
            new("uid", user.Id.ToString()),
            new("pwd_change_required", user.RequiresPasswordChange ? "true" : "false"),
            new("mfa_setup_required", user.IsMfaSetupRequired ? "true" : "false"),
            new("scope", "fhirbridge.full_access")
        };

        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
            claims.Add(new Claim("preferred_username", user.Email));
        }

        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            claims.Add(new Claim(ClaimTypes.Name, user.DisplayName));
            claims.Add(new Claim("name", user.DisplayName));
        }

        foreach (var roleName in roleNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            claims.Add(new Claim(ClaimTypes.Role, roleName));
            claims.Add(new Claim("roles", roleName));
        }

        // Permission codes are deliberately NOT embedded here — see IUserPermissionsProvider. They're
        // resolved per-request instead, from the database (with short-lived caching), keyed off the
        // small "uid" claim already on this token.
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            claims: claims,
            expires: expiresOnUtc,
            signingCredentials: credentials);

        return new AccessTokenDto(
            new JwtSecurityTokenHandler().WriteToken(token),
            "Bearer",
            expiresOnUtc);
    }

    public (string TokenHash, DateTime ExpiresOnUtc) IssueRefreshToken()
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(64);
        var token = Convert.ToBase64String(tokenBytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        var hash = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        var defaultDays = int.TryParse(
            _configuration["Authentication:RefreshTokenLifetimeDays"], out var days)
            ? days
            : 30;
        var refreshLifetimeDays = _settingsCache
            .GetIntAsync("Authentication:RefreshTokenLifetimeDays", defaultDays, default)
            .GetAwaiter().GetResult();

        return (hash, DateTime.UtcNow.AddDays(refreshLifetimeDays));
    }
}
