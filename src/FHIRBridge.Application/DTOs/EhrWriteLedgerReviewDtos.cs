namespace FHIRBridge.Application.DTOs;

/// <summary>
/// One EHR write-back ledger row awaiting a person. Hashes, EHR-side ids, statuses and vendor codes only — the
/// ledger never holds PHI, so neither does this.
/// </summary>
/// <param name="State">The stored state, or "Abandoned" for a Pending row past
/// <see cref="FHIRBridge.Domain.Entities.EhrWriteLedgerState.StalePendingAfter"/>.</param>
public sealed record EhrWriteLedgerReviewItemDto(
    Guid Id,
    Guid TargetConnectionId,
    string? TargetConnectionName,
    string? TargetVendor,
    string ResourceType,
    string State,
    string? TargetResourceId,
    int? HttpStatus,
    string? OutcomeCodes,
    int AttemptCount,
    Guid? DestinationId,
    Guid? WorkflowRunId,
    DateTime CreatedOnUtc,
    DateTime UpdatedOnUtc,
    string? ReviewedBy,
    DateTime? ReviewedOnUtc);

/// <summary>Body of "mark as written": the id the EHR gave the record, as found in the chart.</summary>
public sealed record ResolveEhrWriteAsWrittenRequest(string TargetResourceId);
