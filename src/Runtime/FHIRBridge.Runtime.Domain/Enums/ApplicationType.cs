namespace FHIRBridge.Runtime.Domain.Enums;

/// <summary>
/// The SMART-on-FHIR application profile a source is connected under. This is the <em>composition</em> axis of the
/// Bridge model: it is orthogonal to the vendor (<see cref="RuntimeSourceType"/>) and selects the access-token grant
/// / launch flow, rather than being expressed through subclassing. A given vendor (e.g. Epic) can be connected as
/// any application type; the token-provider strategy is chosen from this value, not from the vendor.
/// </summary>
public enum ApplicationType
{
    /// <summary>
    /// SMART Backend Services — non-interactive system-to-system access using <c>client_credentials</c> with a
    /// signed private-key JWT (RS384). No user is present.
    /// </summary>
    Backend = 0,

    /// <summary>
    /// SMART EHR launch — the app is launched from within the EHR with an <c>iss</c> + <c>launch</c> token and
    /// completes the interactive <c>authorization_code</c> grant.
    /// </summary>
    EhrLaunch = 1,

    /// <summary>
    /// SMART provider standalone launch — the provider launches the app outside the EHR and completes the
    /// interactive <c>authorization_code</c> + PKCE grant.
    /// </summary>
    Standalone = 2,

    /// <summary>
    /// SMART patient / consumer standalone launch — a patient-facing app completing the interactive
    /// <c>authorization_code</c> + PKCE grant with patient-scoped access.
    /// </summary>
    Patient = 3
}
