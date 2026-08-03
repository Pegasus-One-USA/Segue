using System.Security.Claims;
using System.Text;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>Path prefix for every SignalR hub — browsers cannot set an Authorization header on the WebSocket/
    /// SSE handshake a hub connection negotiates, so the client instead appends the access token as
    /// <c>?access_token=...</c> on the connection URL. Only requests under this prefix are allowed to authenticate
    /// that way (see <see cref="ExtractBearerToken"/> and the <c>OnMessageReceived</c> hooks below) — every other
    /// endpoint still requires a real Authorization header.</summary>
    private const string HubPathPrefix = "/hubs";

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

        builder.AddJwtBearer(LocalScheme, _ => { });
        // The signing key is resolved from IAppSecretAccessor (a DI-registered singleton), so it's configured
        // via the named-options DI pipeline rather than captured directly in the AddJwtBearer delegate above —
        // that lets it pick up the value AppSecretProvisioner generates/persists at startup instead of a raw
        // (and, for a self-hosted marketplace install, necessarily hardcoded) appsettings value.
        services.AddOptions<JwtBearerOptions>(LocalScheme)
            .Configure<IAppSecretAccessor>((options, secretAccessor) =>
                ConfigureLocalBearer(options, configuration, environment, secretAccessor));

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
                    var innerTokenValidated = jwtBearerOptions.Events.OnTokenValidated;
                    jwtBearerOptions.Events.OnTokenValidated = async context =>
                    {
                        if (innerTokenValidated is not null)
                        {
                            await innerTokenValidated(context);
                        }

                        ProjectEntraGroupsOntoRoles(context, entra);
                        await ProjectInternalUserIdAsync(context);
                    };

                    var innerMessageReceived = jwtBearerOptions.Events.OnMessageReceived;
                    jwtBearerOptions.Events.OnMessageReceived = async context =>
                    {
                        if (innerMessageReceived is not null)
                        {
                            await innerMessageReceived(context);
                        }

                        ApplyQueryStringTokenForHubs(context);
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
        IWebHostEnvironment environment,
        IAppSecretAccessor secretAccessor)
    {
        var signingKey = secretAccessor.JwtSigningKey;
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

        options.Events ??= new JwtBearerEvents();
        var inner = options.Events.OnMessageReceived;
        options.Events.OnMessageReceived = async context =>
        {
            if (inner is not null)
            {
                await inner(context);
            }

            ApplyQueryStringTokenForHubs(context);
        };
    }

    /// <summary>
    /// A hub connection's WebSocket/SSE handshake can't carry an Authorization header, so its client instead
    /// appends <c>?access_token=...</c> to the connection URL (see <see cref="HubPathPrefix"/>). Every JWT bearer
    /// scheme (Local and, when enabled, Entra) needs this same fallback wired into its own <c>OnMessageReceived</c>
    /// — the policy-scheme selector only decides which scheme to forward to; each forwarded scheme's own handler
    /// still needs <see cref="MessageReceivedContext.Token"/> set itself, or it just sees no Authorization header
    /// and rejects the connection.
    /// </summary>
    private static void ApplyQueryStringTokenForHubs(MessageReceivedContext context)
    {
        if (!string.IsNullOrEmpty(context.Token))
        {
            return;
        }

        if (!context.HttpContext.Request.Path.StartsWithSegments(HubPathPrefix))
        {
            return;
        }

        string? queryToken = context.HttpContext.Request.Query["access_token"];
        if (!string.IsNullOrWhiteSpace(queryToken))
        {
            context.Token = queryToken;
        }
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
        if (!string.IsNullOrWhiteSpace(header) &&
            header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header["Bearer ".Length..].Trim();
        }

        // SignalR's WebSocket/SSE transport can't set a header on the handshake — its client instead appends
        // ?access_token=... to the connection URL (see HubPathPrefix's remarks). Only trusted for that one
        // path prefix, so no other endpoint gains a new way to authenticate via query string.
        if (context.Request.Path.StartsWithSegments(HubPathPrefix))
        {
            string? queryToken = context.Request.Query["access_token"];
            if (!string.IsNullOrWhiteSpace(queryToken))
            {
                return queryToken;
            }
        }

        return null;
    }

    /// <summary>
    /// Entra tokens never carry FHIRBridge's internal Users.Id — only the Entra object id (oid), which is stored
    /// as this user's ExternalUserId. Look the user up by that and stamp a "uid" claim identical in shape to the
    /// one JwtAccessTokenIssuer sets for Local tokens, so CurrentUserClaimReader.GetUserId works the same for
    /// both schemes. Best-effort: an unresolvable user (e.g. Entra login before the account was provisioned in
    /// FHIRBridge) just leaves "uid" absent — CurrentUserInfo.UserId stays null and provenance falls back to
    /// AuditName's email/ExternalUserId path, same as before this existed.
    /// </summary>
    private static async Task ProjectInternalUserIdAsync(TokenValidatedContext context)
    {
        if (context.Principal?.Identity is not ClaimsIdentity identity || identity.HasClaim(c => c.Type == "uid"))
        {
            return;
        }

        var externalUserId = identity.Claims.FirstOrDefault(c =>
            c.Type is "oid" or "http://schemas.microsoft.com/identity/claims/objectidentifier" or ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(externalUserId))
        {
            return;
        }

        var userAccessRepository = context.HttpContext.RequestServices.GetService<IUserAccessRepository>();
        if (userAccessRepository is null)
        {
            return;
        }

        var user = await userAccessRepository.GetUserByExternalIdAsync(externalUserId, context.HttpContext.RequestAborted);
        if (user is not null)
        {
            identity.AddClaim(new Claim("uid", user.Id.ToString()));
        }
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
