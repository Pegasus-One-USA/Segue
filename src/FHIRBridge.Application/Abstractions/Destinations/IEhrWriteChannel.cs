using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// The EHR an <see cref="DestinationType.EhrWriteBack"/> destination writes into, as the writer sees it: the target's
/// identity, the run's write-back options and the handful of calls the writer makes against it. Built by the Runtime
/// plane's EHR write-back node executor over the source connection named by <c>dest_sourceConnectionId</c> and handed
/// to the writer through <see cref="PipelineWriteContext.EhrWriteChannel"/>.
///
/// <para>Typed in Application and Domain types only, the same convention as
/// <see cref="PipelineWriteContext.FetchMissingReferenceAsync"/>: the writer never sees a Runtime connector,
/// token provider or HTTP client.</para>
///
/// <para><b>PHI.</b> Search results and created resources are raw FHIR JSON and stay in memory. Outcome issues
/// carry only codes and element paths, never diagnostics text, because an EHR's diagnostics can echo the
/// demographics that were sent.</para>
/// </summary>
public interface IEhrWriteChannel
{
    /// <summary>The source connection the writes go over.</summary>
    Guid TargetConnectionId { get; }

    SourceSystemType TargetVendor { get; }

    /// <summary>The EHR's FHIR base URL. Keys the idempotency ledger.</summary>
    string TargetBaseUrl { get; }

    /// <summary>The persisted destination's id from the workflow node, when known. The destination the writer
    /// receives is rebuilt on every Runtime run with a fresh id, so it cannot be used for this.</summary>
    Guid? DestinationId { get; }

    EhrWriteBackRunOptions Options { get; }

    /// <summary>The connection says the practice has the vendor's contracted or proprietary write APIs turned on
    /// (<c>SourceConnection.VendorWriteApisActivated</c>). Types that need them are sent only when this is true.</summary>
    bool VendorWriteApisActivated => false;

    /// <summary>The scope string the EHR actually granted the connection's token, or null when the token response
    /// did not echo one.</summary>
    Task<string?> GetGrantedScopeAsync(CancellationToken cancellationToken);

    /// <summary><c>GET {type}?identifier={system}|{value}</c>, first page only.</summary>
    Task<EhrSearchOutcome> SearchByIdentifierAsync(
        string resourceType, string system, string value, CancellationToken cancellationToken);

    /// <summary>Patient-scoped search, e.g. a patient's encounters, first page only.</summary>
    Task<EhrSearchOutcome> SearchForPatientAsync(
        string resourceType, string targetPatientId, CancellationToken cancellationToken);

    /// <summary><c>POST Patient/$match</c> with certain-only matching.</summary>
    Task<EhrPatientMatchOutcome> MatchPatientAsync(string patientJson, CancellationToken cancellationToken);

    /// <summary>One non-idempotent create. Never retried after the request may have reached the EHR. The
    /// <paramref name="resourceJson"/> is what the vendor's write profile shaped: a FHIR resource for a FHIR channel,
    /// or the vendor request it describes for a channel over a proprietary API.</summary>
    Task<EhrCreateOutcome> CreateAsync(string resourceType, string resourceJson, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the encounter a vendor files some records on and that it will not create itself (eClinicalWorks medical
    /// and surgical history need an open telephone encounter). Non-idempotent, like <see cref="CreateAsync"/>. A
    /// channel whose vendor needs none refuses it.
    /// </summary>
    Task<EhrCreateOutcome> CreateHolderEncounterAsync(string targetPatientId, CancellationToken cancellationToken) =>
        Task.FromResult(new EhrCreateOutcome(
            EhrCreateKind.Rejected, null, null, [new EhrOutcomeIssue("error", "not-supported", "holder-encounter-not-supported", null)]));
}

/// <summary>Per-destination write-back options, read from the destination node's <c>dest_*</c> settings.</summary>
/// <param name="DryRun">Shape, validate and resolve every record, send nothing, report what would be written.</param>
/// <param name="CreatePatientIfMissing">Opt-in: create a patient when <c>$match</c> finds none.</param>
/// <param name="MaxWritesPerRun">Upper bound on creates in one write call.</param>
/// <param name="NoteDocStatus">The <c>docStatus</c> clinical notes are filed with: "preliminary" (a clinician
/// reviews and signs them) unless the destination opts into "final".</param>
/// <param name="ResourceTypes">Resource types the destination selected. Records of other types are skipped.</param>
/// <param name="CloneMode">QA only, and only while the <c>EhrWriteBack:CloneModeEnabled</c> system setting is on:
/// every source patient is written as a new, obviously-synthetic test patient (altered name, birth date and
/// identifiers), and its records are filed against that clone. This is the only way to write an EHR's own data back
/// into the same EHR.</param>
/// <param name="CreateHolderEncounter">Opt-in: create the encounter a vendor needs for records it files only on one
/// (eClinicalWorks medical and surgical history). Off, those records are skipped.</param>
/// <param name="TargetProviderId">The EHR's own id for the provider records are filed under, where the vendor API needs
/// one (an eClinicalWorks note's author, an athenaOne document's provider). Null when not configured.</param>
/// <param name="TargetDepartmentId">athenaOne: the department a new patient is registered in, and the department used
/// when a patient's own primary department cannot be read. Null when not configured.</param>
/// <param name="TestAsVendor">A test run: the connection is a Generic FHIR test server standing in for this vendor.
/// Every record goes through the vendor's own capabilities, profiles and channel, and is sent to the test server
/// instead (athenaOne calls to an in-process athenaOne test server that files them there). Null for a real
/// write.</param>
/// <param name="EnabledVariants">Variants the destination turned on (<c>dest_enabledVariants</c>), for the APIs that
/// need it on top of the resource type (<c>EhrWriteCapability.RequiresVariantOptIn</c>).</param>
public sealed record EhrWriteBackRunOptions(
    bool DryRun,
    bool CreatePatientIfMissing,
    int MaxWritesPerRun,
    string NoteDocStatus,
    IReadOnlyList<string> ResourceTypes,
    bool CloneMode = false,
    bool CreateHolderEncounter = false,
    string? TargetProviderId = null,
    string? TargetDepartmentId = null,
    SourceSystemType? TestAsVendor = null,
    IReadOnlyCollection<string>? EnabledVariants = null)
{
    public const int DefaultMaxWritesPerRun = 500;
    public const int MaxAllowedWritesPerRun = 10000;
    public const string PreliminaryDocStatus = "preliminary";
    public const string FinalDocStatus = "final";

    public bool IsTestRun => TestAsVendor is not null;

    public bool IsVariantEnabled(string? variant) =>
        variant is not null && (EnabledVariants?.Contains(variant, StringComparer.Ordinal) ?? false);
}

/// <summary>Codes the write path itself puts on an <see cref="EhrOutcomeIssue"/>.</summary>
public static class EhrWriteOutcomeCodes
{
    /// <summary>The run was cancelled before the create left this process (while waiting for a token, the rate
    /// limiter, or a retry delay): nothing reached the EHR, so the record is retried as is, not left for review.</summary>
    public const string CancelledBeforeSend = "cancelled-before-send";
}

/// <summary>One OperationOutcome issue, reduced to what is safe to log and store.</summary>
/// <param name="VendorCode">The vendor's own code from <c>details.coding</c>, e.g. Epic's "59189".</param>
/// <param name="Expression">The element path the issue is about, e.g. "identifier (ssn)".</param>
public sealed record EhrOutcomeIssue(string? Severity, string? Code, string? VendorCode, string? Expression);

public sealed record EhrSearchOutcome(
    bool Succeeded,
    int? HttpStatus,
    IReadOnlyList<string> Resources,
    IReadOnlyList<EhrOutcomeIssue> Issues);

public enum EhrPatientMatchKind
{
    /// <summary>Exactly one certain match.</summary>
    Certain = 1,

    /// <summary>The EHR knows no such patient.</summary>
    None = 2,

    /// <summary>Several high-confidence or only low-confidence candidates. Needs a person.</summary>
    Ambiguous = 3,

    /// <summary>The call itself failed (configuration, authorization, transport).</summary>
    Failed = 4,
}

public sealed record EhrPatientMatchOutcome(
    EhrPatientMatchKind Kind,
    string? PatientId,
    int? HttpStatus,
    IReadOnlyList<EhrOutcomeIssue> Issues);

public enum EhrCreateKind
{
    Created = 1,

    /// <summary>The EHR answered and refused the record.</summary>
    Rejected = 2,

    /// <summary>The request may have landed: timeout, reset connection, or a 5xx on a create.</summary>
    Unknown = 3,
}

public sealed record EhrCreateOutcome(
    EhrCreateKind Kind,
    int? HttpStatus,
    string? ResourceId,
    IReadOnlyList<EhrOutcomeIssue> Issues);

/// <summary>
/// What one EHR write-back call did, per resource type. Counts and reason codes only — it is stored in run history
/// and returned by run endpoints, so it never carries identifiers, names or resource content.
/// </summary>
/// <param name="ScopeStatus">"verified" when the granted scope covers every type written, "missing:Type,Type" when it
/// does not, "unknown" when the token response echoed no scope.</param>
/// <param name="TestRun">The writes went to a Generic FHIR test server standing in for <paramref name="TargetVendor"/>,
/// not to the vendor.</param>
public sealed record EhrWriteReport(
    bool DryRun,
    string TargetVendor,
    int RecordsReceived,
    string ScopeStatus,
    IReadOnlyList<EhrWriteResourceSummary> Resources,
    bool CloneMode = false,
    bool TestRun = false);

/// <param name="WouldWrite">Records that passed every check; in a dry run, what a live run would send.</param>
/// <param name="AlreadyWritten">Records the ledger shows are already in the EHR (or awaiting review).</param>
/// <param name="Reasons">Reason code -> count for every record not written, e.g. "no-eligible-encounter".</param>
public sealed record EhrWriteResourceSummary(
    string ResourceType,
    int Received,
    int WouldWrite,
    int Written,
    int AlreadyWritten,
    int Skipped,
    int Rejected,
    int Unknown,
    IReadOnlyDictionary<string, int> Reasons);
