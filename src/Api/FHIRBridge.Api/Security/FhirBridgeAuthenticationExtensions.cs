using System.Security.Claims;
using System.Text;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FHIRBridge.Api.Security;

/// <summary>
/// Wires FHIRBridge bearer authentication. A policy scheme inspects the incoming bearer token and
/// forwards it to either the local HS256 scheme (tokens minted by <c>LocalAuthService</c>) or, when
/// configured, the Microsoft Entra ID scheme (Microsoft.Identity.Web). Entra security groups are
/// projected onto the five built-in roles after token validation. With Entra disabled (the default)
/// behaviour is identical to the prior single local JWT bearer setup.
/// </summary>
public static class FhirBridgeAuthenticationExtensions
{
    public const string LocalScheme = "Local";
    public const string EntraScheme = "Entra";
    public const string SelectorScheme = "FhirBridgeSelector";

    public static AuthenticationBuilder AddFhirBridgeAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var entra = configuration.GetSection("Authentication:Entra").Get<EntraAuthenticationOptions>()
            ?? new EntraAuthenticationOptions();

        var builder = services.AddAuthentication(options =>
        {
            options.DefaultScheme = SelectorScheme;
            options.DefaultChallengeScheme = SelectorScheme;
        });

        builder.AddPolicyScheme(SelectorScheme, "Local or Entra bearer", options =>
        {
            options.ForwardDefaultSelector = context =>
                entra.Enabled && LooksLikeEntraToken(context, entra) ? EntraScheme : LocalScheme;
        });

        builder.AddJwtBearer(LocalScheme, options => ConfigureLocalBearer(options, configuration, environment));

        if (entra.Enabled)
        {
            builder.AddMicrosoftIdentityWebApi(
                jwtBearerOptions =>
                {
                    if (!string.IsNullOrWhiteSpace(entra.Audience))
                    {
                        jwtBearerOptions.TokenValidationParameters.ValidAudiences =
                            [entra.Audience, $"api://{entra.Audience}"];
                    }

                    jwtBearerOptions.TokenValidationParameters.RoleClaimType = "roles";
                    jwtBearerOptions.TokenValidationParameters.NameClaimType = "name";

                    jwtBearerOptions.Events ??= new JwtBearerEvents();
                    var inner = jwtBearerOptions.Events.OnTokenValidated;
                    jwtBearerOptions.Events.OnTokenValidated = async context =>
                    {
                        if (inner is not null)
                        {
                            await inner(context);
                        }

                        ProjectEntraGroupsOntoRoles(context, entra);
                    };
                },
                microsoftIdentityOptions =>
                {
                    microsoftIdentityOptions.Instance = entra.Instance;
                    microsoftIdentityOptions.TenantId = entra.TenantId;
                    microsoftIdentityOptions.ClientId = entra.ClientId;
                },
                EntraScheme);
        }

        return builder;
    }

    private static void ConfigureLocalBearer(
        JwtBearerOptions options,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var signingKey = configuration["Authentication:SigningKey"];
        var authority = configuration["Authentication:Authority"];
        var audience = configuration["Authentication:Audience"];
        var validIssuer = configuration["Authentication:ValidIssuer"];

        if (!string.IsNullOrWhiteSpace(authority))
        {
            options.Authority = authority;
        }

        if (!string.IsNullOrWhiteSpace(audience))
        {
            options.Audience = audience;
        }

        options.RequireHttpsMetadata = !environment.IsDevelopment();

        if (!string.IsNullOrWhiteSpace(signingKey))
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                ValidateLifetime = true
            };
        }
        else if (!string.IsNullOrWhiteSpace(validIssuer))
        {
            options.TokenValidationParameters.ValidIssuer = validIssuer;
        }

        options.TokenValidationParameters.RoleClaimType = "roles";
        options.TokenValidationParameters.NameClaimType = "name";
    }

    /// <summary>
    /// Reads (without validating) the bearer token issuer to decide whether the request should be
    /// routed to the Entra scheme. Validation still happens in the forwarded scheme.
    /// </summary>
    private static bool LooksLikeEntraToken(HttpContext context, EntraAuthenticationOptions entra)
    {
        var token = ExtractBearerToken(context);
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        try
        {
            var handler = new JsonWebTokenHandler();
            if (!handler.CanReadToken(token))
            {
                return false;
            }

            var issuer = handler.ReadJsonWebToken(token).Issuer ?? string.Empty;

            return issuer.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase)
                || issuer.Contains("sts.windows.net", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(entra.TenantId)
                    && issuer.Contains(entra.TenantId, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static string? ExtractBearerToken(HttpContext context)
    {
        string? header = context.Request.Headers.Authorization;
        if (string.IsNullOrWhiteSpace(header) ||
            !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return header["Bearer ".Length..].Trim();
    }

    private static void ProjectEntraGroupsOntoRoles(TokenValidatedContext context, EntraAuthenticationOptions entra)
    {
        if (context.Principal?.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        var groups = identity.FindAll("groups").Select(claim => claim.Value);
        var roles = EntraGroupRoleMapper.MapGroupsToRoles(groups, entra.GroupRoleMappings);

        foreach (var role in roles)
        {
            var alreadyPresent = identity.HasClaim("roles", role) ||
                                 identity.HasClaim(ClaimTypes.Role, role);
            if (!alreadyPresent)
            {
                identity.AddClaim(new Claim("roles", role));
            }
        }
    }
}
