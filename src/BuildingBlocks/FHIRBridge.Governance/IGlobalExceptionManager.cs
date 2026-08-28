namespace FHIRBridge.Governance;

/// <summary>
/// Phase 6A – Enterprise Global Exception Management. The single cross-cutting seam every layer funnels
/// unhandled exceptions through. It generates a unique <c>ErrorReferenceId</c>, classifies the exception,
/// persists the full technical detail via <see cref="IGovernanceLogger"/>, and returns a safe
/// <see cref="ErrorReport"/> for the caller to surface. It never throws — capturing an error must not
/// itself fail the request or crash a background service.
/// </summary>
public interface IGlobalExceptionManager
{
    Task<ErrorReport> CaptureAsync(Exception exception, ExceptionContext context, CancellationToken cancellationToken = default);

    /// <summary>Lightweight counterpart to <see cref="CaptureAsync"/> for expected (sub-500) domain outcomes:
    /// no <see cref="Exception"/>/stack trace required, no classification/diagnosis performed. Always recorded
    /// at Severity "Informational" so it stays out of Monitoring → Errors by default while remaining findable
    /// by CorrelationId/ExecutionId/etc. Returns just the reference id — there is no user-facing report to
    /// build, since the caller already has its own safe client message. Returns null if the record could not
    /// actually be persisted (see remarks on <see cref="ErrorReport.ErrorReferenceId"/> — a caller must never
    /// surface a reference id that has no matching ErrorLogs row behind it).</summary>
    Task<string?> CaptureExpectedAsync(ExpectedFailure failure, ExceptionContext context, CancellationToken cancellationToken = default);
}

/// <summary>A routine, expected domain outcome (e.g. a validation rejection, wrong password) that the caller
/// has already decided not to treat as an incident — captured only for CorrelationId-based traceability, not
/// for the Global Exception Manager's classification/diagnosis pipeline.</summary>
public sealed record ExpectedFailure(string ExceptionType, string Message);

/// <summary>Ambient execution context threaded into a captured error. All fields optional — whatever the
/// originating layer knows (an API request has an endpoint/request id; a workflow run has a workflow id).</summary>
public sealed record ExceptionContext(
    string Module,
    string Severity = "Error",
    string? CorrelationId = null,
    string? ExecutionId = null,
    string? WorkflowId = null,
    string? EndpointId = null,
    string? RequestId = null,
    string? TraceId = null,
    string? SpanId = null,
    string? UserFriendlyMessageOverride = null);

/// <summary>The safe, user-facing result of capturing an exception. Contains no stack trace, technical
/// message, or PHI/PII — only the reference id, category, a friendly message, and who should act on it.</summary>
/// <param name="ErrorReferenceId">Null if every persistence attempt failed (e.g. the database was unreachable)
/// — a caller MUST treat a null id as "no reference id to offer", never fall back to a placeholder, since
/// nothing was actually written to ErrorLogs for the caller to quote or for Operations → Errors to find.</param>
public sealed record ErrorReport(
    string? ErrorReferenceId,
    ErrorCategory Category,
    string UserFriendlyMessage,
    string? CorrelationId,
    DiagnosisAction DiagnosisAction = DiagnosisAction.Unknown);
