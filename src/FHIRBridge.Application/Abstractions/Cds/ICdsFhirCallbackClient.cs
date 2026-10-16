using System.Text.Json.Nodes;
using FHIRBridge.Application.DTOs.Cds;

namespace FHIRBridge.Application.Abstractions.Cds;

/// <summary>
/// Fetches prefetch data a CDS client did not send, from the client's own <c>fhirServer</c> with the client's own
/// short-lived <c>fhirAuthorization</c> bearer token (CDS Hooks 2.0 "FHIR resource access"). Implemented in
/// Infrastructure on a typed HttpClient with an SSRF guard; called only by the evaluator, never by a handler.
/// <para>
/// Contract: never throws for a remote failure (each key gets null and the result a short non-PHI reason), never
/// logs or keeps the token, the URLs (they hold patient ids) or any response body, and stops at the deadline.
/// </para>
/// </summary>
public interface ICdsFhirCallbackClient
{
    /// <summary>Runs every query of <paramref name="request"/> in parallel within its timeout.</summary>
    Task<CdsCallbackResult> FetchAsync(CdsCallbackRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// GET <c>{fhirBaseUrl}/metadata</c> with no token and no patient data, to check that the EHR's FHIR server is
    /// reachable from Segue (admin "Test callback"). Same SSRF and scheme rules as <see cref="FetchAsync"/>.
    /// </summary>
    Task<CdsCallbackProbeResult> ProbeMetadataAsync(string fhirBaseUrl, CancellationToken cancellationToken);
}

/// <param name="FhirServer">The request's <c>fhirServer</c>. Already checked against the trusted issuer by the caller.</param>
/// <param name="ExpectedFhirBaseUrl">
/// The trusted issuer's <c>EffectiveFhirBaseUrl</c>. The client refuses (every key null, a failure reason) when
/// <paramref name="FhirServer"/> does not equal it after normalization — a second line of defence.
/// </param>
/// <param name="Authorization">The request's <c>fhirAuthorization</c> (bearer token). Null for an open server.</param>
/// <param name="Queries">
/// Prefetch key → relative FHIR query with the template tokens already resolved (see
/// <see cref="CdsPrefetchTemplate.Resolve"/>), e.g. "allergies" → "AllergyIntolerance?patient=123".
/// </param>
/// <param name="Timeout">Whole-callback budget (Cds:CallbackTimeoutMs, default 900 ms, inside Cds:TimeoutMs).</param>
public sealed record CdsCallbackRequest(
    string FhirServer,
    string ExpectedFhirBaseUrl,
    CdsFhirAuthorization? Authorization,
    IReadOnlyDictionary<string, string> Queries,
    TimeSpan Timeout);

/// <param name="Resources">
/// Prefetch key → the fetched resource or Bundle, or null when that query failed / timed out. Holds PHI: merged
/// into the request's prefetch for the handler, never logged or stored.
/// </param>
/// <param name="ResourceCount">Resources fetched in total (a Bundle counts its entries) — for the call log.</param>
/// <param name="DurationMs">Wall time of the callback — for the call log.</param>
/// <param name="FailureReason">Short non-PHI reason when any key could not be fetched (e.g. "timed out", "HTTP 401"), else null.</param>
public sealed record CdsCallbackResult(
    IReadOnlyDictionary<string, JsonNode?> Resources,
    int ResourceCount,
    long DurationMs,
    string? FailureReason);

/// <param name="Reachable">True when /metadata answered 2xx with a CapabilityStatement.</param>
/// <param name="HttpStatus">The HTTP status, when a response came back.</param>
/// <param name="FhirVersion">CapabilityStatement.fhirVersion, when present.</param>
/// <param name="Software">CapabilityStatement.software.name (and version), when present.</param>
/// <param name="FailureReason">Short reason when not reachable (refused address, timeout, not https, ...).</param>
public sealed record CdsCallbackProbeResult(
    bool Reachable,
    int? HttpStatus,
    string? FhirVersion,
    string? Software,
    long DurationMs,
    string? FailureReason);

/// <summary>Resolves prefetch / fallback query templates against a request's context.</summary>
public static class CdsPrefetchTemplate
{
    /// <summary>
    /// Replaces <c>{{context.name}}</c> tokens (e.g. patientId, encounterId, userId) with the context's string
    /// values, URL-escaped. Returns null when a token has no string value in the context (the key is then not
    /// fetched). <c>{{userPractitionerId}}</c> style tokens are not supported.
    /// </summary>
    public static string? Resolve(string template, JsonObject? context)
    {
        var result = new System.Text.StringBuilder();
        var position = 0;
        while (position < template.Length)
        {
            var start = template.IndexOf("{{", position, StringComparison.Ordinal);
            if (start < 0)
            {
                result.Append(template, position, template.Length - position);
                break;
            }

            var end = template.IndexOf("}}", start + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                return null;
            }

            result.Append(template, position, start - position);
            var token = template[(start + 2)..end].Trim();
            const string prefix = "context.";
            if (!token.StartsWith(prefix, StringComparison.Ordinal)
                || context?[token[prefix.Length..]] is not JsonValue value
                || !value.TryGetValue<string>(out var text)
                || string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            result.Append(Uri.EscapeDataString(text));
            position = end + 2;
        }

        return result.ToString();
    }
}
