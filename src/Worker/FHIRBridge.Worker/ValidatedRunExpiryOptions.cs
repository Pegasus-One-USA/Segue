namespace FHIRBridge.Worker;

/// <summary>
/// Options for the validated-run expiry sweep. Ages out <c>validate-run</c> attempts that were never executed —
/// almost always because the user was redirected to the EHR to sign in and never came back.
/// </summary>
public sealed class ValidatedRunExpiryOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Sweep cadence. Nothing depends on this being prompt — an abandoned attempt is not urgent — so it
    /// runs rarely enough to be invisible.</summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>
    /// How long a Validated attempt may sit unexecuted before it is aged out. Must comfortably exceed a real
    /// interactive EHR sign-in, including a slow login, an MFA prompt, and a user who switches tabs mid-flow —
    /// expiring one of those early would strand a legitimate attempt and force the whole round trip again.
    /// </summary>
    public int ExpireAfterMinutes { get; set; } = 60;
}
