namespace FHIRBridge.Governance;

/// <summary>
/// The single, reusable guardrail that makes raw exception / upstream-response text safe to return to a client.
/// A message is considered client-safe ONLY if it looks like a short, human-written sentence; anything resembling
/// markup (an HTML/XML error page), a stack trace, multi-line output, or an oversized/technical payload is rejected.
///
/// Use this at EVERY point that would otherwise return <c>ex.Message</c>, an external HTTP body, or any untrusted
/// string to a caller — the global exception handler, controller catch blocks, FHIR OperationOutcome builders, and
/// (per the Errors-screen categorization analysis, docs/ERRORS_SCREEN_CATEGORIZATION_ANALYSIS.md §7-8) the pipeline
/// per-resource error path and connection-test results. Centralizing it here (in a BuildingBlocks project every
/// layer can reference — Domain ← Application ← Infrastructure ← Api/Worker, plus BuildingBlocks) means Infrastructure
/// call sites can sanitize the same way the API's global handler does, without a layering violation.
/// </summary>
public static class SafeErrorText
{
    private const int MaxLength = 200;

    /// <summary>Returns the message if it is client-safe; otherwise null.</summary>
    public static string? Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.Length > MaxLength) return null;                          // oversized → likely a dumped payload
        if (raw.IndexOf('<') >= 0 || raw.IndexOf('>') >= 0) return null;  // HTML/XML markup
        if (raw.IndexOf('\n') >= 0 || raw.IndexOf('\r') >= 0) return null;// multi-line → stack/technical output
        if (raw.Contains("Exception", StringComparison.OrdinalIgnoreCase)) return null; // stack-trace-ish
        if (raw.Contains("   at ", StringComparison.Ordinal)) return null;// stack frame
        return raw.Trim();
    }

    /// <summary>Returns the message if it is client-safe; otherwise the supplied generic fallback.</summary>
    public static string SanitizeOr(string? raw, string fallback) => Sanitize(raw) ?? fallback;
}
