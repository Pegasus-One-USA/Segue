namespace FHIRBridge.Application.Messaging;

/// <summary>
/// A request to execute a configured pipeline run, published to the messaging transport by the schedule dispatcher
/// (or a manual trigger) and consumed by the pipeline processor. <see cref="MessageId"/> is the idempotency key.
/// </summary>
public sealed record PipelineRunCommand(
    Guid TenantId,
    IReadOnlyList<string> ResourceTypes,
    bool RunDueSchedulesOnly,
    DateTime? ScheduledAtUtc,
    string? TriggeredBy,
    string? CorrelationId,
    string MessageId)
{
    /// <summary>The specific routes the scheduler claimed as due (catch-up aware). Null for non-scheduled triggers.</summary>
    public IReadOnlyList<Guid>? RouteIds { get; init; }

    /// <summary>When true, the run extracts via FHIR Bulk Data <c>$export</c> instead of <c>$search</c>.</summary>
    public bool UseBulkExport { get; init; }
}
