using FHIRBridge.Application.Security;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Util;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// Builds the <see cref="Saml2Configuration"/> the SAML SP endpoints (login/ACS) bind against, from the
/// system's <see cref="SamlAuthenticationOptions"/>. A single configured IdP per deployment (system-wide,
/// not per-tenant — this platform is single-org, see <c>FHIRBridge.Domain/README.md</c>).
/// </summary>
public interface ISamlConfigurationProvider
{
    Saml2Configuration GetConfiguration();
}

public sealed class SamlConfigurationProvider : ISamlConfigurationProvider
{
    private readonly SamlAuthenticationOptions _options;

    public SamlConfigurationProvider(IOptions<SamlAuthenticationOptions> options)
    {
        _options = options.Value;
    }

    public Saml2Configuration GetConfiguration()
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("SAML single sign-on is not enabled.");
        }

        if (string.IsNullOrWhiteSpace(_options.ServiceProviderEntityId))
        {
            throw new InvalidOperationException("SAML ServiceProviderEntityId is not configured.");
        }

        if (string.IsNullOrWhiteSpace(_options.SingleSignOnUrl))
        {
            throw new InvalidOperationException("SAML SingleSignOnUrl is not configured.");
        }

        if (string.IsNullOrWhiteSpace(_options.IdentityProviderCertificate))
        {
            throw new InvalidOperationException("SAML IdentityProviderCertificate is not configured.");
        }

        var config = new Saml2Configuration
        {
            Issuer = _options.ServiceProviderEntityId,
            SingleSignOnDestination = new Uri(_options.SingleSignOnUrl),
            AllowedIssuer = _options.IdentityProviderEntityId,
            // Most IdPs only require the *response* to be signed (validated below via
            // SignatureValidationCertificates) — signed AuthnRequests are a separate, optional hardening
            // step that needs this SP's own certificate provisioned first. Left off for the initial
            // rollout; ServiceProviderSigningKey is reserved for wiring this up later.
            SignAuthnRequest = false,
            AudienceRestricted = true,
            AllowedAudienceUris = { _options.ServiceProviderEntityId },
            SignatureValidationCertificates = { CertificateUtil.LoadBytes(_options.IdentityProviderCertificate) },
            // SAML trust is established by pinning the IdP's exact certificate (exchanged out-of-band via
            // metadata), not by chain-validating it against a public CA — most enterprise IdP signing certs
            // are self-signed or issued by a private/internal CA, so the framework's ChainTrust default
            // would reject them even though the pinned cert itself is exactly right.
            CertificateValidationMode = System.ServiceModel.Security.X509CertificateValidationMode.None,
        };

        return config;
    }
}
