namespace FHIRBridge.Governance;

/// <summary>
/// A configuration/entity change to be written to the immutable, hash-chained audit trail.
/// Actor, IP address, user agent, and correlation id are sourced ambiently by the logger
/// implementation (from <c>ICurrentUserService</c>) — callers only describe what happened.
/// </summary>
public sealed record AuditEntry(
    string Module,
    string Action,
    string? EntityType = null,
    string? EntityId = null,
    string? EntityName = null,
    string? OldValueJson = null,
    string? NewValueJson = null,
    string Status = "Success",
    string? Remarks = null,
    string? CorrelationId = null);

/// <summary>A patient/resource access event — recorded PHI-free (identifiers only, never clinical content).</summary>
public sealed record DataAccessEntry(
    string ResourceType,
    string? ResourceId,
    string Action,
    string? PatientId = null,
    string? Purpose = null,
    Guid? PipelineRunId = null,
    string? CorrelationId = null);

/// <summary>A login/logout/MFA/token event.</summary>
public sealed record AuthenticationEntry(
    string AuthenticationType,
    bool Success,
    string? UserEmail = null,
    string? FailureReason = null,
    string? CorrelationId = null);

/// <summary>An RBAC authorization decision — callers write this on denial, the compliance-relevant case.</summary>
public sealed record AuthorizationEntry(
    string RequestPath,
    string PermissionCode,
    string Result,
    string? UserEmail = null,
    string? CorrelationId = null);

/// <summary>A security-relevant event outside normal authentication/config flows (lockouts, anomalies, denials).</summary>
public sealed record SecurityEventEntry(
    string EventType,
    string Severity,
    string? UserEmail = null,
    string? Details = null,
    string? CorrelationId = null);

/// <summary>A scheduler dispatch decision — the scheduler recognized due work and handed it off.</summary>
public sealed record SchedulerRunEntry(
    string SchedulerId,
    string Status,
    int RouteCount,
    string? CorrelationId = null);

/// <summary>One retry attempt against a transient failure.</summary>
public sealed record RetryEntry(
    string Context,
    int RetryNumber,
    int DelayMilliseconds,
    string Reason,
    string? CorrelationId = null);

/// <summary>An unhandled exception, captured centrally. The Phase 6A fields (reference id, category,
/// user-friendly message, and the execution-correlation set) are populated by the Global Exception Manager;
/// call sites that construct an <see cref="ErrorEntry"/> directly may leave them null.</summary>
public sealed record ErrorEntry(
    string Severity,
    string ExceptionType,
    string Message,
    string? StackTrace = null,
    string? Module = null,
    string? CorrelationId = null,
    string? ErrorReferenceId = null,
    string? Category = null,
    string? UserFriendlyMessage = null,
    string? ExecutionId = null,
    string? WorkflowId = null,
    string? EndpointId = null,
    string? RequestId = null,
    string? TraceId = null,
    string? SpanId = null,
    DiagnosisAction? DiagnosisAction = null,
    string? DiagnosisCause = null);

/// <summary>One outbound HTTP call — method/URL/status/duration only, never headers, tokens, or bodies.</summary>
public sealed record ApiRequestEntry(
    string Method,
    string Url,
    int? StatusCode,
    long DurationMs,
    string? Error = null,
    string? CorrelationId = null);

/// <summary>One destination write ("export") completing.</summary>
public sealed record ExportEntry(
    string DestinationName,
    string Format,
    int RowCount,
    string Status,
    long? FileSizeBytes = null,
    Guid? PipelineRunId = null,
    string? CorrelationId = null);

/// <summary>One outbound notification (currently: export-delivery email).</summary>
public sealed record NotificationEntry(
    string NotificationType,
    string Recipient,
    string Status,
    string? Subject = null,
    string? Error = null,
    string? CorrelationId = null);

/// <summary>A resource that produced US-Core/data-quality validation warnings during normalization.</summary>
public sealed record ValidationFailureEntry(
    string ResourceType,
    string? ResourceId,
    IReadOnlyCollection<string> Warnings,
    double? DataQualityScore = null,
    Guid? PipelineRunId = null,
    string? CorrelationId = null);

/// <summary>One periodic connectivity check against a configured endpoint.</summary>
public sealed record EndpointHealthEntry(
    string EndpointName,
    string EndpointType,
    string Status,
    long LatencyMs,
    string? Message = null);

/// <summary>A SMART on FHIR launch completing (or failing) — no FHIRBridge portal user is involved, this is
/// the EHR/patient authorizing a source connection's data access. <see cref="GrantedScope"/>/
/// <see cref="PatientContextGranted"/>/<see cref="TokenCacheKeyHash"/> are populated for interactive
/// (EHR-launch/standalone) sign-ins only — they diagnose whether this launch actually established the patient
/// context a later unscoped Patient search depends on, and let a save-time key be compared against a later
/// lookup-time key without ever printing the underlying CallerId/session identifier.</summary>
public sealed record SmartLaunchEntry(
    Guid SourceConnectionId,
    string SourceName,
    string LaunchType,
    bool Success,
    string? FailureReason = null,
    string? GrantedScope = null,
    bool? PatientContextGranted = null,
    string? TokenCacheKeyHash = null,
    string? CorrelationId = null);
