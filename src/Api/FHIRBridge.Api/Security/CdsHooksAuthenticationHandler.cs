using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Api.Security;

/// <summary>
/// The "CdsHooks" scheme the selector forwards every /cds-services request to. It deliberately authenticates no
/// one: the bearer token on those calls is an EHR-signed CDS Hooks JWT, not a portal token, so it must never reach
/// the Local/Entra JWT handlers (which would reject and log it) nor pick up a stray portal session cookie. The CDS
/// JWT itself is validated per call inside ICdsHooksService (CdsJwtValidator), because the expected audience is the
/// exact service URL called, the issuer allow-list is per service instance, and every rejection must be written to
/// the CDS call log.
/// </summary>
public sealed class CdsHooksAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public CdsHooksAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());
}
