using FHIRBridge.Application.Abstractions.Caching;

namespace FHIRBridge.Api.Security;

/// <summary>
/// Validates a caller-supplied <c>callerId</c> (the URL a launch-url minting endpoint should redirect back to once
/// OAuth completes) against the same live, DB-backed allowed-origins set CORS itself enforces (see
/// <see cref="IAllowedCorsOriginsCache"/>) — not just the static <c>Portal:AllowedOrigins</c> config floor, so an
/// origin added through the admin Settings screen is honored immediately. An anonymous launch-url endpoint honoring
/// an unchecked <c>callerId</c> is an open-redirect risk (a real Epic login could be followed by a redirect to an
/// attacker-controlled page), so the caller's claimed origin must be one this API already trusts enough to call it
/// from.
/// </summary>
public static class CallerIdOriginValidator
{
    public static async Task<bool> IsAllowedOriginAsync(
        string callerId, IAllowedCorsOriginsCache originsCache, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(callerId, UriKind.Absolute, out var callerUri))
        {
            return false;
        }

        var allowedOrigins = await originsCache.GetOriginsAsync(cancellationToken);
        var callerOrigin = callerUri.GetLeftPart(UriPartial.Authority);

        return allowedOrigins.Any(origin =>
            Uri.TryCreate(origin, UriKind.Absolute, out var allowedUri)
            && string.Equals(allowedUri.GetLeftPart(UriPartial.Authority), callerOrigin, StringComparison.OrdinalIgnoreCase));
    }
}
