using FHIRBridge.Runtime.Application.DTOs;

namespace FHIRBridge.Runtime.Application.Abstractions.Connectors;

/// <summary>
/// FHIR REST writes and the lookups write-back needs, over the same connection a source client reads through.
/// Implemented by <c>FhirSourceConnectorBase</c>, so every vendor client that derives from it has it; callers detect
/// it with <c>client is IFhirWriteClient</c>, the way they detect <see cref="IResourceExtractionDiagnostics"/>.
/// Being able to send a write says nothing about whether a vendor accepts it: that is <c>EhrWriteCapabilities</c>.
/// </summary>
public interface IFhirWriteClient
{
    /// <summary>
    /// <c>POST {base}/{resourceType}</c> with an explicit <c>Prefer</c>. Not idempotent: retried only on 429, and on a
    /// 503 that carries Retry-After, which are refusals the server sends before doing any work. Anything else that
    /// leaves the outcome open (timeout, dropped connection, other 5xx) returns
    /// <see cref="FhirWriteOutcomeKind.OutcomeUnknown"/> after one attempt.
    /// </summary>
    Task<FhirWriteResult> CreateAsync(
        string resourceType,
        string resourceJson,
        bool returnRepresentation,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken);

    /// <summary><c>GET {base}/{resourceType}?identifier={system}|{value}</c>, first page. The identifier value is never
    /// logged.</summary>
    Task<FhirSearchPage> SearchByIdentifierAsync(
        string resourceType,
        string system,
        string value,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken);

    /// <summary><c>GET {base}/{resourceType}?patient={patientId}</c>, first page.</summary>
    Task<FhirSearchPage> SearchForPatientAsync(
        string resourceType,
        string patientId,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken);

    /// <summary><c>POST {base}/Patient/$match</c> with <c>onlyCertainMatches</c> as the boolean true. Read-only, so it is
    /// retried like a read.</summary>
    Task<FhirPatientMatchResult> MatchPatientAsync(
        string patientJson,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken);
}
