using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Security;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Util;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// Builds the <see cref="Saml2Configuration"/> the SAML SP endpoints (login/ACS) bind against. Every
/// field is resolved live via <see cref="ISystemSettingsCache"/> (DB-backed "SSO Configurations" admin
/// screen), falling back to the <see cref="SamlAuthenticationOptions"/> appsettings value only when no
/// override row exists — same live-read pattern as <c>LocalAuthService.ResolveLockoutPolicyAsync</c>, so
/// a change saved from the admin screen takes effect on the very next request, no restart needed.
/// A single configured IdP per deployment (system-wide, not per-tenant — this platform is single-org,
/// see <c>FHIRBridge.Domain/README.md</c>).
/// </summary>
public interface ISamlConfigurationProvider
{
    Task<Saml2Configuration> GetConfigurationAsync(CancellationToken cancellationToken);
}

public sealed class SamlConfigurationProvider : ISamlConfigurationProvider
{
    private const string EnabledKey = "Authentication:Saml:Enabled";
    private const string ServiceProviderEntityIdKey = "Authentication:Saml:ServiceProviderEntityId";
    private const string IdentityProviderEntityIdKey = "Authentication:Saml:IdentityProviderEntityId";
    private const string SingleSignOnUrlKey = "Authentication:Saml:SingleSignOnUrl";
    private const string IdentityProviderCertificateKey = "Authentication:Saml:IdentityProviderCertificate";

    private readonly SamlAuthenticationOptions _fallback;
    private readonly ISystemSettingsCache _settingsCache;

    public SamlConfigurationProvider(IOptions<SamlAuthenticationOptions> options, ISystemSettingsCache settingsCache)
    {
        _fallback = options.Value;
        _settingsCache = settingsCache;
    }

    public async Task<Saml2Configuration> GetConfigurationAsync(CancellationToken cancellationToken)
    {
        var enabled = await _settingsCache.GetBoolAsync(EnabledKey, _fallback.Enabled, cancellationToken);
        if (!enabled)
        {
            throw new InvalidOperationException("SAML single sign-on is not enabled.");
        }

        var serviceProviderEntityId = await _settingsCache.GetStringAsync(
            ServiceProviderEntityIdKey, _fallback.ServiceProviderEntityId ?? string.Empty, cancellationToken);
        if (string.IsNullOrWhiteSpace(serviceProviderEntityId))
        {
            throw new InvalidOperationException("SAML ServiceProviderEntityId is not configured.");
        }

        var singleSignOnUrl = await _settingsCache.GetStringAsync(
            SingleSignOnUrlKey, _fallback.SingleSignOnUrl ?? string.Empty, cancellationToken);
        if (string.IsNullOrWhiteSpace(singleSignOnUrl))
        {
            throw new InvalidOperationException("SAML SingleSignOnUrl is not configured.");
        }

        var identityProviderCertificate = await _settingsCache.GetStringAsync(
            IdentityProviderCertificateKey, _fallback.IdentityProviderCertificate ?? string.Empty, cancellationToken);
        if (string.IsNullOrWhiteSpace(identityProviderCertificate))
        {
            throw new InvalidOperationException("SAML IdentityProviderCertificate is not configured.");
        }

        var identityProviderEntityId = await _settingsCache.GetStringAsync(
            IdentityProviderEntityIdKey, _fallback.IdentityProviderEntityId ?? string.Empty, cancellationToken);

        var config = new Saml2Configuration
        {
            Issuer = serviceProviderEntityId,
            SingleSignOnDestination = new Uri(singleSignOnUrl),
            AllowedIssuer = identityProviderEntityId,
            // Most IdPs only require the *response* to be signed (validated below via
            // SignatureValidationCertificates) — signed AuthnRequests are a separate, optional hardening
            // step that needs this SP's own certificate provisioned first. Left off for the initial
            // rollout; ServiceProviderSigningKey is reserved for wiring this up later.
            SignAuthnRequest = false,
            AudienceRestricted = true,
            AllowedAudienceUris = { serviceProviderEntityId },
            SignatureValidationCertificates = { CertificateUtil.LoadBytes(identityProviderCertificate) },
            // SAML trust is established by pinning the IdP's exact certificate (exchanged out-of-band via
            // metadata), not by chain-validating it against a public CA — most enterprise IdP signing certs
            // are self-signed or issued by a private/internal CA, so the framework's ChainTrust default
            // would reject them even though the pinned cert itself is exactly right.
            CertificateValidationMode = System.ServiceModel.Security.X509CertificateValidationMode.None,
        };

        return config;
    }
}
