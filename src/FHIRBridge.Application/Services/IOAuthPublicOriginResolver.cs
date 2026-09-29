namespace FHIRBridge.Application.Services;

/// <summary>
/// Resolves the origin (scheme + host, no path) OAuth redirect_uri/launch/authorize/standalone URLs are built
/// from. Shared by every controller that builds one of those URLs (OAuthController, PatientStandaloneLaunchController)
/// so there is exactly one place that decides "configured OAuth:PublicBaseUrl, or the request's own origin" —
/// see OAuthPublicOriginResolver's remarks for why the request's own origin is not trustworthy behind a WAF.
/// </summary>
public interface IOAuthPublicOriginResolver
{
    /// <param name="requestDerivedOrigin">
    /// The caller's own best-effort origin (typically <c>$"{Request.Scheme}://{Request.Host}"</c>) — used only
    /// when OAuth:PublicBaseUrl isn't configured. Taken as a plain string, not HttpContext/HttpRequest, so this
    /// can be unit-tested without any ASP.NET Core request plumbing.
    /// </param>
    Task<string> ResolveAsync(string requestDerivedOrigin, CancellationToken cancellationToken);
}
