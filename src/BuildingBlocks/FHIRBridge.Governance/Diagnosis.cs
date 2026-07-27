namespace FHIRBridge.Governance;

/// <summary>
/// Who should act on a captured failure. Separate from <see cref="ErrorCategory"/> (which answers "what kind of
/// exception is this") because the same category can fall on either side — a Database exception can be the
/// customer's own destination or FHIRBridge's control-plane DB; a Business exception can be a real bug or a
/// routine "your configuration is incomplete" case. Category alone can't answer "who fixes it".
/// </summary>
public enum DiagnosisAction
{
    /// <summary>The customer can resolve this themselves (bad credentials, unreachable endpoint, missing config).</summary>
    SelfFix = 0,

    /// <summary>Not something the customer can act on — needs the FHIRBridge team.</summary>
    ContactSupport = 1,

    /// <summary>No rule matched; not enough signal to say either way.</summary>
    Unknown = 2,
}

/// <summary>
/// The result of diagnosing a captured exception: a plain-language cause and who should act on it. Computed once,
/// at the point of capture, so the message text and any UI badge are always sourced from the same value instead of
/// being independently guessed in two places.
/// </summary>
public sealed record Diagnosis(string Cause, DiagnosisAction Action);
