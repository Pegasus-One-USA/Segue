using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using Microsoft.IdentityModel.Tokens;

namespace FHIRBridge.Api.Security;

public sealed class JwtAccessTokenIssuer : IAccessTokenIssuer
{
    private readonly IConfiguration _configuration;

    public JwtAccessTokenIssuer(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public AccessTokenDto Issue(User user, IReadOnlyCollection<string> roleNames)
    {
        var signingKey = _configuration["Authentication:SigningKey"];
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            throw new InvalidOperationException("Authentication:SigningKey configuration is missing.");
        }

        var expiresOnUtc = DateTime.UtcNow.AddMinutes(
            int.TryParse(_configuration["Authentication:TokenLifetimeMinutes"], out var minutes)
                ? minutes
                : 480);

        var claims = new List<Claim>
        {
            new("oid", user.ExternalUserId),
            new(ClaimTypes.NameIdentifier, user.ExternalUserId),
            new("pwd_change_required", user.MustChangePassword ? "true" : "false"),
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
}
