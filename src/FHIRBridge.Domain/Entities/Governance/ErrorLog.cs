using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Governance;

/// <summary>Immutable record of an unhandled exception, captured centrally (Api global handler, Worker hosts,
/// Runtime workflow engine). Phase 6A extends this with a unique <see cref="ErrorReferenceId"/>, an
/// <see cref="Category"/>, a user-safe message, and the full execution-correlation set so a support engineer
/// can locate one error and see all of its context. Resolution state (Open/Resolved) is tracked separately in
/// <see cref="ErrorResolution"/> so this forensic record stays append-only.</summary>
public sealed class ErrorLog : Entity<Guid>, IAppendOnlyEntity
{
    private ErrorLog()
    {
    }

    public ErrorLog(
        Guid id,
        DateTime occurredOnUtc,
        string severity,
        string exceptionType,
        string message,
        string? stackTrace,
        string? module,
        string? correlationId,
        string? errorReferenceId = null,
        string? category = null,
        string? userFriendlyMessage = null,
        string? executionId = null,
        string? workflowId = null,
        string? endpointId = null,
        string? requestId = null,
        string? traceId = null,
        string? spanId = null,
        string? diagnosisAction = null,
        string? diagnosisCause = null)
    {
        Id = id;
        OccurredOnUtc = occurredOnUtc;
        Severity = severity;
        ExceptionType = exceptionType;
        Message = message;
        StackTrace = stackTrace;
        Module = module;
        CorrelationId = correlationId;
        ErrorReferenceId = errorReferenceId;
        Category = category;
        UserFriendlyMessage = userFriendlyMessage;
        ExecutionId = executionId;
        WorkflowId = workflowId;
        EndpointId = endpointId;
        RequestId = requestId;
        TraceId = traceId;
        SpanId = spanId;
        DiagnosisAction = diagnosisAction;
        DiagnosisCause = diagnosisCause;
    }

    public DateTime OccurredOnUtc { get; private set; }
    public string Severity { get; private set; } = default!;
    public string ExceptionType { get; private set; } = default!;
    public string Message { get; private set; } = default!;
    public string? StackTrace { get; private set; }
    public string? Module { get; private set; }
    public string? CorrelationId { get; private set; }

    // ── Phase 6A – Enterprise Global Exception Management ────────────────────────
    /// <summary>Globally unique, human-quotable reference (e.g. <c>ERR-20260721-000123</c>) returned to the
    /// end user and searchable from Monitoring → Errors.</summary>
    public string? ErrorReferenceId { get; private set; }

    /// <summary>Persisted <see cref="ErrorCategory"/> name.</summary>
    public string? Category { get; private set; }

    /// <summary>The safe message shown to the end user — never contains PHI/PII or technical detail.</summary>
    public string? UserFriendlyMessage { get; private set; }

    public string? ExecutionId { get; private set; }
    public string? WorkflowId { get; private set; }
    public string? EndpointId { get; private set; }
    public string? RequestId { get; private set; }
    public string? TraceId { get; private set; }
    public string? SpanId { get; private set; }

    /// <summary>Persisted <c>DiagnosisAction</c> name (SelfFix/ContactSupport/Unknown) — who should act on this
    /// error, computed once by <c>IFailureDiagnosisClassifier</c> at capture time. Stored as a plain string (like
    /// <see cref="Category"/>) rather than referencing the FHIRBridge.Governance enum directly, since Domain must
    /// not depend on that building block.</summary>
    public string? DiagnosisAction { get; private set; }

    /// <summary>Plain-language cause paired with <see cref="DiagnosisAction"/> — sourced from the same computation
    /// as <see cref="UserFriendlyMessage"/>, so message text and any UI badge can't disagree.</summary>
    public string? DiagnosisCause { get; private set; }
}
