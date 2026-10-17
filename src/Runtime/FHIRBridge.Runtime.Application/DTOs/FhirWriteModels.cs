namespace FHIRBridge.Runtime.Application.DTOs;

public enum FhirWriteOutcomeKind
{
    /// <summary>2xx: the resource was created.</summary>
    Created = 1,

    /// <summary>The server answered and refused the request (4xx other than 429).</summary>
    Rejected = 2,

    /// <summary>The request may have reached the server and its result is not known: a timeout, a dropped
    /// connection, a 5xx, or retries for 429 ran out. A create must never be retried blindly after this.</summary>
    OutcomeUnknown = 3,
}

/// <summary>One OperationOutcome issue. Diagnostics text is deliberately absent: an EHR's diagnostics can echo the
/// submitted demographics, and nothing in this record is meant to need masking.</summary>
/// <param name="DetailCodes">Codes from <c>details.coding</c>, e.g. Epic's "59189".</param>
/// <param name="Expressions">The <c>expression</c> (or legacy <c>location</c>) element paths.</param>
public sealed record FhirOperationOutcomeIssue(
    string? Severity,
    string? Code,
    IReadOnlyList<string> DetailCodes,
    IReadOnlyList<string> Expressions);

/// <param name="ResourceId">The id from the Location header (relative or absolute), else from a returned body.</param>
/// <param name="ResponseJson">The returned resource when the server sent one (return=representation).</param>
public sealed record FhirWriteResult(
    FhirWriteOutcomeKind Kind,
    int? StatusCode,
    string? ResourceId,
    string? VersionId,
    string? ResponseJson,
    IReadOnlyList<FhirOperationOutcomeIssue> Issues);

/// <param name="Resources">Raw JSON of every non-OperationOutcome entry on the first page.</param>
public sealed record FhirSearchPage(
    bool Succeeded,
    int? StatusCode,
    IReadOnlyList<string> Resources,
    IReadOnlyList<FhirOperationOutcomeIssue> Issues);

public enum FhirPatientMatchKind
{
    Certain = 1,
    None = 2,
    Ambiguous = 3,
    Failed = 4,
}

/// <param name="PatientId">Set only for <see cref="FhirPatientMatchKind.Certain"/>.</param>
public sealed record FhirPatientMatchResult(
    FhirPatientMatchKind Kind,
    string? PatientId,
    int? StatusCode,
    IReadOnlyList<FhirOperationOutcomeIssue> Issues);

/// <summary>
/// One request a vendor's write API needs that is not a plain FHIR create: an eClinicalWorks transaction Bundle POSTed
/// to the FHIR base URL, or a call to athenaOne's proprietary REST API.
/// </summary>
/// <param name="Method">The HTTP method name, e.g. "POST".</param>
/// <param name="Url">Absolute URL.</param>
/// <param name="Body">A raw body (with <paramref name="ContentType"/>); null when <paramref name="FormFields"/> is used.</param>
/// <param name="FormFields">Form fields, sent url-encoded, or as multipart/form-data when <paramref name="Multipart"/>.</param>
/// <param name="Idempotent">True for reads (retried like a search); false for writes, which are sent once.</param>
/// <param name="AddSourceQueryParameters">Append the connector's own per-request query parameters (athenahealth FHIR's
/// <c>ah-practice</c>); false for a non-FHIR API that does not take them.</param>
public sealed record FhirRawWriteRequest(
    string Method,
    string Url,
    string? Body = null,
    string? ContentType = null,
    IReadOnlyDictionary<string, string>? FormFields = null,
    bool Multipart = false,
    bool Idempotent = false,
    bool AddSourceQueryParameters = true);

/// <param name="Kind"><see cref="FhirWriteOutcomeKind.Created"/> for any 2xx, which the caller interprets from
/// <paramref name="Body"/>; otherwise as for a create.</param>
/// <param name="Body">The response body, held in memory only: it may carry PHI and is never logged.</param>
public sealed record FhirRawWriteResult(
    FhirWriteOutcomeKind Kind,
    int? StatusCode,
    string? Body,
    IReadOnlyList<FhirOperationOutcomeIssue> Issues);
