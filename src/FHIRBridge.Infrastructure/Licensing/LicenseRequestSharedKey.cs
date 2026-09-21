namespace FHIRBridge.Infrastructure.Licensing;

/// <summary>
/// AES-256 key shared between every FHIRBridge install and the licensor's intake tooling
/// (FHIRBridge-LicenseServer, a separate repository — see that repo's own copy of this same class), used
/// only to encode <see cref="LicenseRequestPayloadEncoder"/>'s manual-fallback blob when the direct
/// <c>POST /api/v1/license-request</c> outbound call can't reach the licensor.
///
/// This is NOT a defense against a sophisticated attacker even when configured with a real, rotated
/// secret — the real integrity property this feature relies on is <c>LicenseRequest.UniqueKey</c> being
/// checked against the license's own <c>requestKey</c> claim at apply time (see
/// <c>LicenseService.ApplyAsync</c>), which this key plays no part in. Its only job is keeping the
/// copy-pasted fallback blob opaque and tamper-evident for support purposes (so an admin, or anyone who
/// intercepts the blob in transit — e.g. a forwarded support email — can't casually edit the embedded
/// contact details or <c>UniqueKey</c> without invalidating the AES-GCM auth tag), not cryptographically
/// unforgeable against someone who has this actual key value.
///
/// <see cref="KeyBase64"/> resolves from <see cref="EnvVar"/> when set — same pattern as
/// <c>ILicenseService</c>'s own <c>FHIRBRIDGE_LICENSE</c>/<c>FHIRBRIDGE_LICENSE_FILE</c> env-var
/// resolution and <c>LicenseSigningKeyProvider</c>'s PFX-or-embedded-dev-key fallback in
/// FHIRBridge-LicenseServer: a real deployment sets a real, non-committed 32-byte (base64-encoded) secret
/// via the env var, generated fresh and shared with the licensor out of band — NEVER checked into source
/// control. <see cref="DevPlaceholderKeyBase64"/> below is used only when the env var is unset, and is
/// explicitly a dev-only fallback, not a production default: it is committed to source (and mirrored in
/// FHIRBridge-LicenseServer's own copy), so anyone with either repo can read it, and every install that
/// hasn't set the env var shares the exact same value. <see cref="IsUsingDevPlaceholder"/> lets a caller
/// with a logger (see <c>LicenseRequestService</c>'s constructor) warn once when that's the case.
///
/// Rotating a real, configured key requires updating BOTH sides (this env var here, and the matching
/// FHIRBridge-LicenseServer deployment's own) in lockstep — an old blob encoded with a since-rotated key
/// simply fails to decode, which is safe: it only fails the manual-fallback path, never the direct API
/// call or license activation.
/// </summary>
public static class LicenseRequestSharedKey
{
    /// <summary>Base64-encoded 32-byte AES-256 key. Set this on every install (and the matching
    /// FHIRBridge-LicenseServer deployment) for anything beyond local development.</summary>
    private const string EnvVar = "FHIRBRIDGE_LICENSE_REQUEST_SHARED_KEY";

    /// <summary>DEV-ONLY — see this class's remarks. Never treat this as a real secret.</summary>
    private const string DevPlaceholderKeyBase64 = "N4vRYHvMVtduSsBjRly3kJKsXJyAxTjXrC0tL8S53hA=";

    private static readonly string? ConfiguredKeyBase64 = Environment.GetEnvironmentVariable(EnvVar);

    /// <summary>True when no <see cref="EnvVar"/> value was found and the code fell back to the
    /// committed, publicly-known dev placeholder.</summary>
    public static bool IsUsingDevPlaceholder => string.IsNullOrWhiteSpace(ConfiguredKeyBase64);

    public static string KeyBase64 => IsUsingDevPlaceholder ? DevPlaceholderKeyBase64 : ConfiguredKeyBase64!;
}
