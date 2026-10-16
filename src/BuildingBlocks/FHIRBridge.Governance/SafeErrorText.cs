using System.Text.RegularExpressions;

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

    private static readonly Regex UrlPattern = new(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MarkupPattern = new(@"<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// The user-facing form of an upstream failure text, for a message that must still say what went wrong (a status
    /// code, a reason) rather than be replaced by a generic fallback as <see cref="Sanitize"/> does. Used for a bulk
    /// export's job and run errors — which reach the Errors screen, the Bulk Data Status popup and the run-status event
    /// every connected browser receives — and for the popup's live error. A failed download's message names the file
    /// URL, and for a source whose file URLs are pre-signed (manifest requiresAccessToken: false) that URL alone grants
    /// access to the bulk NDJSON; a source's error body can echo URLs and is often a whole HTML or XML page.
    /// <para>Every URL is cut to its scheme and host (a port kept), markup is dropped, whitespace — newlines included —
    /// collapsed, and the result capped at 300 characters. URLs are cut before the cap, so it can never leave half a
    /// URL behind. Keep the full text in the logs.</para>
    /// </summary>
    public static string Redact(string message)
    {
        var withoutUrls = UrlPattern.Replace(message, match =>
            Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
                ? uri.GetLeftPart(UriPartial.Authority)
                : "[link removed]");
        var plain = WhitespacePattern.Replace(MarkupPattern.Replace(withoutUrls, " "), " ").Trim();
        return plain.Length > 300 ? plain[..300] + "…" : plain;
    }
}
