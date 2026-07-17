namespace FHIRBridge.Api.Security;

/// <summary>
/// Validates a caller-supplied <c>callerId</c> (the URL a launch-url minting endpoint should redirect back to once
/// OAuth completes) against <c>Portal:AllowedOrigins</c> — the same CORS allow-list already used to decide which
/// browser origins may call this API. Reused here rather than introducing a separate, database-backed allow-list:
/// an anonymous launch-url endpoint honoring an unchecked <c>callerId</c> is an open-redirect risk (a real Epic
/// login could be followed by a redirect to an attacker-controlled page), so the caller's claimed origin must be
/// one this API already trusts enough to call it from.
/// </summary>
public static class CallerIdOriginValidator
{
    public static bool IsAllowedOrigin(string callerId, IConfiguration configuration)
    {
        if (!Uri.TryCreate(callerId, UriKind.Absolute, out var callerUri))
        {
            return false;
        }

        var allowedOrigins = configuration.GetSection("Portal:AllowedOrigins").Get<string[]>() ?? [];
        var callerOrigin = callerUri.GetLeftPart(UriPartial.Authority);

        return allowedOrigins.Any(origin =>
            Uri.TryCreate(origin, UriKind.Absolute, out var allowedUri)
            && string.Equals(allowedUri.GetLeftPart(UriPartial.Authority), callerOrigin, StringComparison.OrdinalIgnoreCase));
    }
}
