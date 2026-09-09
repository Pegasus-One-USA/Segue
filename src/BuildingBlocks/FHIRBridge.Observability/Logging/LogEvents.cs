using Microsoft.Extensions.Logging;

namespace FHIRBridge.Observability.Logging;

/// <summary>
/// The stable <see cref="EventId"/> catalog for every FHIRBridge lifecycle stage that emits a structured log —
/// first-time setup through to a destination write. Seq derives its own <c>@i</c> event-type hash from the message
/// template, which changes whenever the wording does; logging an explicit <see cref="EventId"/> alongside it gives
/// dashboards and saved queries a filter (<c>EventId.Id = 3101</c>) that survives copy edits to the template.
/// <para>
/// Ranges are grouped by lifecycle stage so a whole stage can be selected at once
/// (<c>EventId.Id &gt;= 4000 and EventId.Id &lt; 5000</c> = every source/EHR event):
/// </para>
/// <list type="table">
/// <item><term>1xxx</term><description>Configuration / first-time setup (tenant, source, destination, mapping)</description></item>
/// <item><term>2xxx</term><description>Scheduling (worker ticks, due evaluation, time zone resolution, dispatch)</description></item>
/// <item><term>3xxx</term><description>Workflow run and node lifecycle</description></item>
/// <item><term>4xxx</term><description>Source / EHR (auth, discovery, extraction, paging, incremental cursor)</description></item>
/// <item><term>5xxx</term><description>Governance, de-identification and transform</description></item>
/// <item><term>6xxx</term><description>Destination writes</description></item>
/// </list>
/// </summary>
public static class LogEvents
{
    // ── 1xxx Configuration / first-time setup ───────────────────────────────────────────────────────
    public static readonly EventId SourceConnectionCreated = new(1001, nameof(SourceConnectionCreated));
    public static readonly EventId SourceConnectionUpdated = new(1002, nameof(SourceConnectionUpdated));
    public static readonly EventId SourceConnectionDeleted = new(1003, nameof(SourceConnectionDeleted));
    public static readonly EventId DestinationCreated = new(1011, nameof(DestinationCreated));
    public static readonly EventId DestinationUpdated = new(1012, nameof(DestinationUpdated));
    public static readonly EventId DestinationDeleted = new(1013, nameof(DestinationDeleted));
    public static readonly EventId MappingProfileSaved = new(1021, nameof(MappingProfileSaved));
    public static readonly EventId RouteSaved = new(1031, nameof(RouteSaved));
    public static readonly EventId WorkflowDefinitionSaved = new(1041, nameof(WorkflowDefinitionSaved));
    public static readonly EventId ConnectionTestCompleted = new(1051, nameof(ConnectionTestCompleted));

    // ── 2xxx Scheduling ─────────────────────────────────────────────────────────────────────────────
    public static readonly EventId SchedulerTickCompleted = new(2001, nameof(SchedulerTickCompleted));
    public static readonly EventId SchedulerDisabled = new(2002, nameof(SchedulerDisabled));

    /// <summary>SystemSettings could not be read, so every DB-backed setting fell back to its compiled-in default
    /// for that call — including <c>RuntimeWorker:Enabled=false</c>, which stops the scheduler silently.</summary>
    public static readonly EventId SystemSettingsUnavailable = new(2003, nameof(SystemSettingsUnavailable));
    public static readonly EventId WorkflowScheduleEvaluated = new(2011, nameof(WorkflowScheduleEvaluated));
    public static readonly EventId WorkflowScheduleDue = new(2012, nameof(WorkflowScheduleDue));
    // 2021 was a "time zone resolved" event; it is deliberately not defined. Successful resolution is reported as
    // the TimeZoneId/TimeZoneResolved/TimeZoneOffset properties on the schedule-evaluation events above, where it is
    // useful in context, rather than as an event of its own that would fire on every tick.
    public static readonly EventId TimeZoneResolutionFailed = new(2022, nameof(TimeZoneResolutionFailed));

    // Queue-based dispatch (ScheduleDispatcher → PipelineRunCommandProcessor / WebhookIngestionCommandProcessor).
    // MessageId is the join key across all four, including across a transport's retries of the same message.
    public static readonly EventId MessageProcessorStarted = new(2031, nameof(MessageProcessorStarted));
    public static readonly EventId MessageConsumed = new(2032, nameof(MessageConsumed));
    public static readonly EventId MessageCompleted = new(2033, nameof(MessageCompleted));
    public static readonly EventId MessageFailed = new(2034, nameof(MessageFailed));

    // ── 3xxx Workflow run / node lifecycle ──────────────────────────────────────────────────────────
    public static readonly EventId WorkflowRunStarted = new(3001, nameof(WorkflowRunStarted));
    public static readonly EventId WorkflowRunSucceeded = new(3002, nameof(WorkflowRunSucceeded));
    public static readonly EventId WorkflowRunPartiallySucceeded = new(3003, nameof(WorkflowRunPartiallySucceeded));
    public static readonly EventId WorkflowRunFailed = new(3004, nameof(WorkflowRunFailed));
    public static readonly EventId WorkflowRunCancelled = new(3005, nameof(WorkflowRunCancelled));
    public static readonly EventId WorkflowRunAwaitingBulkExport = new(3006, nameof(WorkflowRunAwaitingBulkExport));
    public static readonly EventId WorkflowRunResumed = new(3007, nameof(WorkflowRunResumed));
    public static readonly EventId NodeExecutionStarted = new(3101, nameof(NodeExecutionStarted));
    public static readonly EventId NodeExecutionCompleted = new(3102, nameof(NodeExecutionCompleted));
    public static readonly EventId NodeExecutionFailed = new(3103, nameof(NodeExecutionFailed));

    // ── 4xxx Source / EHR ───────────────────────────────────────────────────────────────────────────
    public static readonly EventId SourceTokenAcquired = new(4001, nameof(SourceTokenAcquired));
    public static readonly EventId SourceTokenFailed = new(4002, nameof(SourceTokenFailed));
    public static readonly EventId SourceResolved = new(4011, nameof(SourceResolved));
    public static readonly EventId SourceExtractionStarted = new(4021, nameof(SourceExtractionStarted));
    public static readonly EventId SourceExtractionCompleted = new(4022, nameof(SourceExtractionCompleted));
    public static readonly EventId ResourceTypeExtracted = new(4031, nameof(ResourceTypeExtracted));
    public static readonly EventId ResourceTypeSkipped = new(4032, nameof(ResourceTypeSkipped));
    public static readonly EventId ResourceTypeRetried = new(4033, nameof(ResourceTypeRetried));
    public static readonly EventId IncrementalWatermarkApplied = new(4041, nameof(IncrementalWatermarkApplied));
    public static readonly EventId IncrementalWatermarkAdvanced = new(4042, nameof(IncrementalWatermarkAdvanced));
    public static readonly EventId BulkExportSubmitted = new(4051, nameof(BulkExportSubmitted));
    public static readonly EventId BulkExportCompleted = new(4052, nameof(BulkExportCompleted));

    // ── 5xxx Governance / transform ─────────────────────────────────────────────────────────────────
    public static readonly EventId GovernanceApplied = new(5001, nameof(GovernanceApplied));
    public static readonly EventId TransformCompleted = new(5011, nameof(TransformCompleted));

    // ── 7xxx Authentication, session and user administration ────────────────────────────────────────
    // Keyed on UserId, never on the login address: "email" is in PhiRedactor.DefaultMaskedProperties, so an
    // Email property would render as *** in every sink anyway. The address is still written to the governance
    // AuthenticationLog table for the compliance trail — these events are the operational view, not that trail.
    public static readonly EventId LoginSucceeded = new(7001, nameof(LoginSucceeded));
    public static readonly EventId LoginFailed = new(7002, nameof(LoginFailed));
    public static readonly EventId AccountLockedOut = new(7003, nameof(AccountLockedOut));
    public static readonly EventId MfaChallengeIssued = new(7004, nameof(MfaChallengeIssued));
    public static readonly EventId LogoutCompleted = new(7005, nameof(LogoutCompleted));
    public static readonly EventId PasswordChanged = new(7011, nameof(PasswordChanged));
    public static readonly EventId PasswordResetRequested = new(7012, nameof(PasswordResetRequested));
    public static readonly EventId PasswordResetCompleted = new(7013, nameof(PasswordResetCompleted));
    public static readonly EventId MagicLinkRequested = new(7014, nameof(MagicLinkRequested));
    public static readonly EventId MagicLinkRedeemed = new(7015, nameof(MagicLinkRedeemed));
    public static readonly EventId TokenRefreshed = new(7016, nameof(TokenRefreshed));
    public static readonly EventId UserCreated = new(7021, nameof(UserCreated));
    public static readonly EventId UserUpdated = new(7022, nameof(UserUpdated));
    public static readonly EventId UserRoleChanged = new(7023, nameof(UserRoleChanged));
    public static readonly EventId SuperAdminSetupCompleted = new(7031, nameof(SuperAdminSetupCompleted));

    // ── 6xxx Destination ────────────────────────────────────────────────────────────────────────────
    public static readonly EventId DestinationWriteStarted = new(6001, nameof(DestinationWriteStarted));
    public static readonly EventId DestinationWriteCompleted = new(6002, nameof(DestinationWriteCompleted));
    public static readonly EventId DestinationWriteFailed = new(6003, nameof(DestinationWriteFailed));
}
