using System.Security.Claims;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Security;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.MvcCore;
using ITfoxtec.Identity.Saml2.Schemas;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[Authorize]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IUserAccessService _userAccessService;
    private readonly ILocalAuthService _localAuthService;
    private readonly ISsoAuthService _ssoAuthService;
    private readonly ISetupService _setupService;
    private readonly IConfiguration _configuration;
    private readonly ISamlConfigurationProvider _samlConfigurationProvider;
    private readonly SamlAuthenticationOptions _samlOptions;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IUserAccessService userAccessService,
        ILocalAuthService localAuthService,
        ISsoAuthService ssoAuthService,
        ISetupService setupService,
        IConfiguration configuration,
        ISamlConfigurationProvider samlConfigurationProvider,
        IOptions<SamlAuthenticationOptions> samlOptions,
        ILogger<AuthController> logger)
    {
        _userAccessService = userAccessService;
        _localAuthService = localAuthService;
        _ssoAuthService = ssoAuthService;
        _setupService = setupService;
        _configuration = configuration;
        _samlConfigurationProvider = samlConfigurationProvider;
        _logger = logger;
        _samlOptions = samlOptions.Value;
    }

    /// <summary>First-run check — true when the deployment still needs its initial SuperAdmin created.</summary>
    [HttpGet("setup-status")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(SetupStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSetupStatus(CancellationToken cancellationToken)
    {
        var requiresSetup = await _setupService.RequiresSetupAsync(cancellationToken);

        return Ok(new SetupStatusDto(requiresSetup));
    }

    /// <summary>
    /// First-run creation of the sole SuperAdmin (local/password). Anonymous but one-shot: returns 409 once any user
    /// exists. On success returns a signed-in session.
    /// </summary>
    [HttpPost("setup-superadmin")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(LocalLoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetupSuperAdmin(
        [FromBody] CreateFirstSuperAdminRequest request,
        CancellationToken cancellationToken)
    {
        if (!await _setupService.RequiresSetupAsync(cancellationToken))
        {
            return Conflict(new { error = "setup_already_completed", message = "A user already exists; setup is complete." });
        }

        var response = await _setupService.CreateFirstSuperAdminAsync(request, cancellationToken);

        return Ok(IssueTokenCookiesAndStrip(response));
    }

    /// <summary>
    /// SSO token exchange: validate an external IdP (Entra/Google) token and return a FHIRBridge session
    /// for a known, enabled user. Returns 401 when no matching enabled account exists.
    /// </summary>
    [HttpPost("sso/login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(LocalLoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SsoLogin(
        [FromBody] SsoLoginRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _ssoAuthService.LoginAsync(request, cancellationToken);

        return Ok(IssueTokenCookiesAndStrip(response));
    }

    /// <summary>
    /// First-run creation of the sole SuperAdmin via an external IdP identity (no password). One-shot:
    /// returns 409 once any user exists.
    /// </summary>
    [HttpPost("setup-superadmin-sso")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(LocalLoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetupSuperAdminSso(
        [FromBody] CreateFirstSuperAdminSsoRequest request,
        CancellationToken cancellationToken)
    {
        if (!await _setupService.RequiresSetupAsync(cancellationToken))
        {
            return Conflict(new { error = "setup_already_completed", message = "A user already exists; setup is complete." });
        }

        var response = await _setupService.CreateFirstSuperAdminViaSsoAsync(request, cancellationToken);

        return Ok(IssueTokenCookiesAndStrip(response));
    }

    /// <summary>Public SSO configuration for the portal: which providers are enabled and their client settings.</summary>
    [HttpGet("/api/v1/config")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(SsoConfigDto), StatusCodes.Status200OK)]
    public IActionResult GetSsoConfig()
    {
        var entra = _configuration.GetSection("Authentication:Entra");
        var google = _configuration.GetSection("Authentication:Google");

        var entraEnabled = entra.GetValue<bool>("Enabled");
        var instance = (entra["Instance"] ?? "https://login.microsoftonline.com/").TrimEnd('/');
        var tenantId = entra["TenantId"];
        var authority = entraEnabled && !string.IsNullOrWhiteSpace(tenantId)
            ? $"{instance}/{tenantId}"
            : null;

        var dto = new SsoConfigDto(
            new SsoEntraConfigDto(entraEnabled, authority, entra["ClientId"]),
            new SsoGoogleConfigDto(google.GetValue<bool>("Enabled"), google["ClientId"]),
            new SsoSamlConfigDto(_samlOptions.Enabled),
            new SsoMagicLinkConfigDto(_configuration.GetValue<bool>("LocalAuth:MagicLink:Enabled")));

        return Ok(dto);
    }

    /// <summary>Redirects the browser to the configured SAML IdP's Single Sign-On endpoint with a signed AuthnRequest.</summary>
    [HttpGet("saml/login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public IActionResult SamlLogin()
    {
        if (!_samlOptions.Enabled)
        {
            return NotFound();
        }

        var config = _samlConfigurationProvider.GetConfiguration();
        var authnRequest = new Saml2AuthnRequest(config)
        {
            AssertionConsumerServiceUrl = new Uri(BuildAcsUrl()),
        };

        var binding = new Saml2RedirectBinding();
        return binding.Bind(authnRequest).ToActionResult();
    }

    /// <summary>
    /// SAML Assertion Consumer Service: receives the IdP's signed assertion via HTTP-POST binding, validates
    /// it, and completes sign-in for the linked/known user — same user-resolution and audit-log tail as
    /// <see cref="SsoLogin"/>, via <see cref="ISsoAuthService.LoginWithIdentityAsync"/>. This is a top-level
    /// browser navigation (not an XHR the portal's JS can catch), so outcomes are conveyed by redirecting
    /// into the portal rather than a JSON response.
    /// </summary>
    [HttpPost("saml/acs")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> SamlAssertionConsumerService(CancellationToken cancellationToken)
    {
        if (!_samlOptions.Enabled)
        {
            return NotFound();
        }

        try
        {
            var config = _samlConfigurationProvider.GetConfiguration();
            var binding = new Saml2PostBinding();
            var saml2AuthnResponse = new Saml2AuthnResponse(config);
            var genericRequest = Request.ToGenericHttpRequest(validate: true);

            binding.ReadSamlResponse(genericRequest, saml2AuthnResponse);
            if (saml2AuthnResponse.Status != Saml2StatusCodes.Success)
            {
                return RedirectToPortalError($"saml_status_{saml2AuthnResponse.Status}");
            }

            binding.Unbind(genericRequest, saml2AuthnResponse);

            var claims = saml2AuthnResponse.ClaimsIdentity;
            var subject = claims.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrWhiteSpace(subject))
            {
                return RedirectToPortalError("saml_no_subject");
            }

            var email = claims.FindFirst(ClaimTypes.Email)?.Value
                ?? claims.FindFirst(ClaimTypes.Upn)?.Value
                ?? claims.FindFirst("mail")?.Value
                ?? claims.FindFirst("email")?.Value
                ?? string.Empty;
            var name = claims.FindFirst(ClaimTypes.Name)?.Value;

            var identity = new ExternalIdentity(LoginProvider.Saml, subject, email, name);
            var response = await _ssoAuthService.LoginWithIdentityAsync(identity, cancellationToken);

            IssueTokenCookiesAndStrip(response);
            return Redirect(_samlOptions.PortalRedirectUrl ?? "/");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "SAML ACS: no enabled account matched the asserted identity.");
            return RedirectToPortalError("saml_no_account");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SAML ACS: assertion processing failed.");
            return RedirectToPortalError("saml_failed");
        }
    }

    /// <summary>Serves this app's SAML SP metadata so a hospital IT team can configure their IdP to trust it.</summary>
    [HttpGet("saml/metadata")]
    [AllowAnonymous]
    public IActionResult SamlMetadata()
    {
        if (!_samlOptions.Enabled)
        {
            return NotFound();
        }

        var acsUrl = BuildAcsUrl();
        var entityId = _samlOptions.ServiceProviderEntityId;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <EntityDescriptor xmlns="urn:oasis:names:tc:SAML:2.0:metadata" entityID="{entityId}">
              <SPSSODescriptor protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol" AuthnRequestsSigned="false" WantAssertionsSigned="true">
                <NameIDFormat>urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress</NameIDFormat>
                <AssertionConsumerService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST" Location="{acsUrl}" index="0" isDefault="true" />
              </SPSSODescriptor>
            </EntityDescriptor>
            """;

        return Content(xml, "application/samlmetadata+xml");
    }

    /// <summary>Requests a passwordless "sign-in link" email.</summary>
    [HttpPost("magic-link/request")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(MagicLinkResponse), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> RequestMagicLink(
        [FromBody] MagicLinkRequest request,
        CancellationToken cancellationToken)
    {
        if (!_configuration.GetValue<bool>("LocalAuth:MagicLink:Enabled"))
        {
            return NotFound();
        }

        var response = await _localAuthService.RequestMagicLinkAsync(request, cancellationToken);

        return Accepted(response);
    }

    /// <summary>Redeems a magic-link token to complete sign-in.</summary>
    [HttpPost("magic-link/redeem")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(LocalLoginResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> RedeemMagicLink(
        [FromBody] MagicLinkRedeemRequest request,
        CancellationToken cancellationToken)
    {
        if (!_configuration.GetValue<bool>("LocalAuth:MagicLink:Enabled"))
        {
            return NotFound();
        }

        var response = await _localAuthService.RedeemMagicLinkAsync(request, cancellationToken);

        return Ok(IssueTokenCookiesAndStrip(response));
    }

    private string BuildAcsUrl() => $"{Request.Scheme}://{Request.Host}/api/v1/auth/saml/acs";

    private IActionResult RedirectToPortalError(string errorCode)
    {
        var baseUrl = _samlOptions.PortalErrorRedirectUrl ?? "/";
        var separator = baseUrl.Contains('?') ? "&" : "?";
        return Redirect($"{baseUrl}{separator}error={errorCode}");
    }

    [HttpGet("me")]
    [ProducesResponseType(typeof(UserProfileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCurrentUser(CancellationToken cancellationToken)
    {
        var profile = await _userAccessService.GetCurrentUserProfileAsync(cancellationToken);

        return Ok(profile);
    }

    [HttpPost("login")]
    [ProducesResponseType(typeof(UserProfileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> RecordLogin(CancellationToken cancellationToken)
    {
        var profile = await _userAccessService.RecordLoginAsync(cancellationToken);

        return Ok(profile);
    }

    [HttpPost("internal/login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(LocalLoginResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> LocalLogin(
        [FromBody] LocalLoginRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _localAuthService.LoginAsync(request, cancellationToken);

        return Ok(IssueTokenCookiesAndStrip(response));
    }

    [HttpPost("internal/login/mfa")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(LocalLoginResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> CompleteMfaLogin(
        [FromBody] MfaLoginRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _localAuthService.CompleteMfaLoginAsync(request, cancellationToken);

        return Ok(IssueTokenCookiesAndStrip(response));
    }

    [HttpPost("internal/change-password")]
    [ProducesResponseType(typeof(LocalLoginResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _localAuthService.ChangePasswordAsync(request, cancellationToken);

        return Ok(IssueTokenCookiesAndStrip(response));
    }

    [HttpPost("internal/forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(ForgotPasswordResponse), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ForgotPassword(
        [FromBody] ForgotPasswordRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _localAuthService.ForgotPasswordAsync(request, cancellationToken);
        if (!_configuration.GetValue<bool>("LocalAuth:ExposeResetTokens"))
        {
            response = response with { ResetToken = null, ExpiresOnUtc = null };
        }

        return Accepted(response);
    }

    [HttpPost("internal/reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ResetPassword(
        [FromBody] ResetPasswordRequest request,
        CancellationToken cancellationToken)
    {
        await _localAuthService.ResetPasswordAsync(request, cancellationToken);

        return NoContent();
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(LocalLoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(
        [FromBody] RefreshTokenRequest? request,
        CancellationToken cancellationToken)
    {
        // HIPAA #7: the refresh token now lives in an HttpOnly cookie, not the request body — the body param
        // is kept only so an already-in-flight caller from before this change doesn't 400 on a stale contract.
        var refreshToken = Request.Cookies[RefreshTokenCookieName] ?? request?.RefreshToken;
        if (string.IsNullOrEmpty(refreshToken))
        {
            return Unauthorized();
        }

        var response = await _localAuthService.RefreshTokenAsync(new RefreshTokenRequest(refreshToken), cancellationToken);

        return Ok(IssueTokenCookiesAndStrip(response));
    }

    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        await _localAuthService.LogoutAsync(cancellationToken);

        ClearTokenCookies();

        return NoContent();
    }

    private const string AccessTokenCookieName = "fhirbridge_access_token";
    private const string RefreshTokenCookieName = "fhirbridge_refresh_token";
    internal const string CsrfCookieName = "fhirbridge_csrf";

    /// <summary>
    /// HIPAA #7: moves the issued tokens out of the JSON body into HttpOnly cookies (mitigates XSS-driven token
    /// theft) and returns the same response with the raw token fields nulled out — everything else (Profile,
    /// RequiresMfa, etc.) is unchanged so existing frontend code that reads those fields keeps working.
    /// </summary>
    private LocalLoginResponse IssueTokenCookiesAndStrip(LocalLoginResponse response)
    {
        if (response.AccessToken is not null && response.ExpiresOnUtc is not null)
        {
            Response.Cookies.Append(AccessTokenCookieName, response.AccessToken, CookieOptionsFor(response.ExpiresOnUtc.Value));
        }

        if (response.RefreshToken is not null && response.RefreshTokenExpiresOnUtc is not null)
        {
            Response.Cookies.Append(RefreshTokenCookieName, response.RefreshToken, CookieOptionsFor(response.RefreshTokenExpiresOnUtc.Value));

            // Double-submit CSRF token: readable by the portal's JS (NOT HttpOnly) so it can echo it back as a
            // header on state-changing requests — cookie auth alone can't prove the request came from our own
            // page, since browsers attach cookies to cross-site requests too.
            var csrfOptions = CookieOptionsFor(response.RefreshTokenExpiresOnUtc.Value);
            csrfOptions.HttpOnly = false;
            Response.Cookies.Append(CsrfCookieName, Guid.NewGuid().ToString("N"), csrfOptions);
        }

        return response with { AccessToken = null, RefreshToken = null };
    }

    private void ClearTokenCookies()
    {
        Response.Cookies.Delete(AccessTokenCookieName, new CookieOptions { Path = "/" });
        Response.Cookies.Delete(RefreshTokenCookieName, new CookieOptions { Path = "/" });
        Response.Cookies.Delete(CsrfCookieName, new CookieOptions { Path = "/" });
    }

    private CookieOptions CookieOptionsFor(DateTime expiresOnUtc) => new()
    {
        HttpOnly = true,
        // Secure is required for SameSite=Strict cookies in modern browsers, but a hardcoded `true` would make
        // the cookie silently vanish on a plain-HTTP local `dotnet run` — tie it to the actual request scheme.
        Secure = Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresOnUtc, DateTimeKind.Utc)),
        Path = "/",
    };
}
