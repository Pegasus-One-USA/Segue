namespace FHIRBridge.Api.Security;

/// <summary>
/// Marks an endpoint as exempt from the double-submit CSRF check in <c>Program.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// The CSRF check originally keyed its exemption off <see cref="Microsoft.AspNetCore.Authorization.IAllowAnonymous"/>,
/// on the reasoning that only cookie-AUTHENTICATED state-changing requests need protecting. That works for a
/// genuinely anonymous endpoint, but it silently misses an endpoint that is authorized some OTHER way than by the
/// portal session cookie — most importantly <c>POST /workflows/{id}/run</c>, which a third-party app (Demo_TestApp)
/// calls cross-origin and which authorizes via the unguessable <c>callerId</c> token-cache key in its body.
/// </para>
/// <para>
/// Such an endpoint cannot carry <c>[AllowAnonymous]</c> (a portal-triggered run must still satisfy
/// <c>workflow.run</c>), so it kept failing the CSRF check the moment a portal cookie happened to ride along. That
/// is not hypothetical: the portal and the third-party app are same-site (<c>seguedemo.pegasusone.com</c> and
/// <c>segue.pegasusone.com:3011</c> share the <c>pegasusone.com</c> registrable domain, and SameSite ignores both
/// port and subdomain), so <c>SameSite=Strict</c> does NOT withhold the cookie. Any user with a portal session open
/// in the same browser sent it, flipped the check on, and got a 403 for a token a third-party app can never have —
/// which is exactly why this broke intermittently rather than for everyone at once.
/// </para>
/// <para>
/// Declaring the exemption explicitly, instead of inferring it from the absence of authentication, means an endpoint
/// opts out only when someone says so in writing — and the exemption can no longer silently miss its intended
/// target. Apply this ONLY where the request carries its own non-cookie credential; a cookie-authenticated
/// state-changing endpoint must keep the check.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class CsrfExemptAttribute : Attribute
{
}
