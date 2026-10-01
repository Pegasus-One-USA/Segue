using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>Lifecycle states of one EHR write-back ledger row.</summary>
public static class EhrWriteLedgerState
{
    /// <summary>Claimed just before the request is sent. A row still Pending after its run ended means the process
    /// died mid-send, so it is treated like <see cref="Unknown"/>.</summary>
    public const string Pending = "Pending";

    /// <summary>The EHR answered 201; <see cref="EhrWriteLedgerEntry.TargetResourceId"/> holds its id.</summary>
    public const string Written = "Written";

    /// <summary>The EHR said the record is already there (a duplicate-record or reading-already-exists outcome), or
    /// a patient was resolved to an existing EHR patient. Never retried.</summary>
    public const string AlreadyAtTarget = "AlreadyAtTarget";

    /// <summary>The EHR refused the record (4xx with an OperationOutcome). Retried only after a person fixes the
    /// cause and releases it.</summary>
    public const string Rejected = "Rejected";

    /// <summary>The request may or may not have landed (timeout, connection reset, 5xx on a create). Never retried
    /// automatically, because the EHR files a replayed create a second time; it waits on the review list.</summary>
    public const string Unknown = "Unknown";

    public static bool BlocksResend(string state) =>
        state is Pending or Written or AlreadyAtTarget or Unknown;
}

/// <summary>
/// One source record's write into one EHR environment — the only duplicate guard write-back has, because the EHR
/// supports no conditional create and files a replayed allergy, problem or note a second time.
///
/// <para><b>Keyed on the EHR environment, not on connection or destination ids.</b> Cloning a workflow clones its
/// source connections and destinations with new ids, and the Runtime rebuilds the destination with a fresh id on
/// every run. <see cref="TargetKey"/> is a hash of the EHR's normalised base URL, so a clone that writes to the same
/// Epic environment still finds the rows of the original. <see cref="SourceKey"/> is a hash of where the record
/// came from, so a source record maps to at most one row per environment and resource type.</para>
///
/// <para><b>Never holds PHI.</b> Only hashes, FHIR ids of the EHR's own records, HTTP statuses and vendor outcome
/// codes. Not audited (the change audit trail would snapshot every state change) and not soft-deleted (a deleted
/// row would hide a write from the duplicate check).</para>
/// </summary>
public sealed class EhrWriteLedgerEntry : Entity<Guid>
{
    private EhrWriteLedgerEntry()
    {
    }

    public EhrWriteLedgerEntry(
        string targetKey,
        Guid targetConnectionId,
        string resourceType,
        string sourceKey,
        string contentHash,
        EhrWriteOperation operation,
        Guid? destinationId,
        Guid? workflowRunId,
        DateTime createdOnUtc)
    {
        Id = Guid.NewGuid();
        TargetKey = targetKey;
        TargetConnectionId = targetConnectionId;
        ResourceType = resourceType;
        SourceKey = sourceKey;
        ContentHash = contentHash;
        Operation = operation;
        DestinationId = destinationId;
        WorkflowRunId = workflowRunId;
        State = EhrWriteLedgerState.Pending;
        AttemptCount = 0;
        CreatedOnUtc = createdOnUtc;
        UpdatedOnUtc = createdOnUtc;
    }

    /// <summary>SHA-256 (uppercase hex) of the EHR's normalised FHIR base URL.</summary>
    public string TargetKey { get; private set; } = default!;

    /// <summary>The connection the write went over. Informational: the key is <see cref="TargetKey"/>.</summary>
    public Guid TargetConnectionId { get; private set; }

    public string ResourceType { get; private set; } = default!;

    /// <summary>SHA-256 (uppercase hex) of the record's origin: source base URL, resource type and source id.</summary>
    public string SourceKey { get; private set; } = default!;

    /// <summary>SHA-256 (uppercase hex) of the shaped resource that was sent, so a later run can tell the source
    /// record changed after it was written.</summary>
    public string ContentHash { get; private set; } = default!;

    public EhrWriteOperation Operation { get; private set; }

    public string State { get; private set; } = default!;

    /// <summary>The EHR's id for the record, from the Location header. Null until written.</summary>
    public string? TargetResourceId { get; private set; }

    public int? HttpStatus { get; private set; }

    /// <summary>Vendor outcome codes only (e.g. Epic's "59189"), never diagnostics text, which can echo the
    /// demographics that were sent.</summary>
    public string? OutcomeCodes { get; private set; }

    public int AttemptCount { get; private set; }

    /// <summary>The persisted destination's id from the workflow node, when the write came from a workflow.</summary>
    public Guid? DestinationId { get; private set; }

    public Guid? WorkflowRunId { get; private set; }

    public DateTime CreatedOnUtc { get; private set; }

    public DateTime UpdatedOnUtc { get; private set; }

    /// <summary>Claims the row for a send. <paramref name="contentHash"/> is what is about to be sent, which differs
    /// from the stored one when a rejected record is retried after it changed.</summary>
    public void MarkSending(string contentHash, DateTime utcNow)
    {
        ContentHash = contentHash;
        State = EhrWriteLedgerState.Pending;
        AttemptCount++;
        UpdatedOnUtc = utcNow;
    }

    public void MarkWritten(string targetResourceId, int httpStatus, DateTime utcNow) =>
        Settle(EhrWriteLedgerState.Written, targetResourceId, httpStatus, outcomeCodes: null, utcNow);

    public void MarkAlreadyAtTarget(string? targetResourceId, int? httpStatus, string? outcomeCodes, DateTime utcNow) =>
        Settle(EhrWriteLedgerState.AlreadyAtTarget, targetResourceId, httpStatus, outcomeCodes, utcNow);

    public void MarkRejected(int? httpStatus, string? outcomeCodes, DateTime utcNow) =>
        Settle(EhrWriteLedgerState.Rejected, TargetResourceId, httpStatus, outcomeCodes, utcNow);

    public void MarkUnknown(int? httpStatus, string? outcomeCodes, DateTime utcNow) =>
        Settle(EhrWriteLedgerState.Unknown, TargetResourceId, httpStatus, outcomeCodes, utcNow);

    private void Settle(string state, string? targetResourceId, int? httpStatus, string? outcomeCodes, DateTime utcNow)
    {
        State = state;
        TargetResourceId = targetResourceId;
        HttpStatus = httpStatus;
        OutcomeCodes = outcomeCodes is { Length: > 200 } ? outcomeCodes[..200] : outcomeCodes;
        UpdatedOnUtc = utcNow;
    }
}
