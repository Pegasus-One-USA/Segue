namespace FHIRBridge.Infrastructure.Cds;

/// <summary>
/// Runtime knobs for the public CDS Hooks endpoints, bound from the "Cds" configuration section. The time budget
/// can also be overridden live through System Settings (key <see cref="TimeoutMsKey"/>).
/// </summary>
public sealed class CdsOptions
{
    public const string SectionName = "Cds";
    public const string TimeoutMsKey = "Cds:TimeoutMs";

    /// <summary>Time budget for one handler evaluation; past it the call answers with no cards.</summary>
    public int TimeoutMs { get; set; } = 1500;

    /// <summary>Allowed clock difference for exp/nbf/iat.</summary>
    public int ClockSkewSeconds { get; set; } = 60;

    /// <summary>How long a fetched JWKS is reused before it is fetched again.</summary>
    public int JwksCacheMinutes { get; set; } = 60;

    /// <summary>Timeout of one JWKS fetch. Keeps an unreachable key endpoint from eating the call's budget.</summary>
    public int JwksFetchTimeoutSeconds { get; set; } = 5;

    /// <summary>A token whose exp is further out than this is refused (CDS Hooks 2.0 asks for 5 minutes or less).</summary>
    public int MaxTokenLifetimeMinutes { get; set; } = 60;

    /// <summary>
    /// Lets JWKS URLs resolve to private, loopback or link-local addresses. Off by default so an admin-entered JWKS
    /// URL cannot reach internal-only endpoints; turn on only for an EHR on the same private network.
    /// </summary>
    public bool AllowPrivateJwksAddresses { get; set; }

    public const string CallbackTimeoutMsKey = "Cds:CallbackTimeoutMs";

    /// <summary>
    /// Budget for fetching missing prefetch from the client's fhirServer (all keys in parallel); kept inside
    /// <see cref="TimeoutMs"/>. Past it the missing keys stay missing and the call is answered Degraded.
    /// </summary>
    public int CallbackTimeoutMs { get; set; } = 900;

    /// <summary>
    /// Lets a callback use plain http. Off by default: the request's bearer token would travel in clear text. Dev only.
    /// </summary>
    public bool AllowInsecureCallback { get; set; }

    /// <summary>
    /// Lets callback URLs resolve to private, loopback or link-local addresses (same guard as JWKS). Off by default.
    /// </summary>
    public bool AllowPrivateCallbackAddresses { get; set; }

    /// <summary>Largest callback response read per query; a bigger one is treated as not fetched.</summary>
    public int CallbackMaxResponseBytes { get; set; } = 4 * 1024 * 1024;
}
