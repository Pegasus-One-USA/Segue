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
