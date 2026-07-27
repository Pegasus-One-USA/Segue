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
}

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
public sealed record ErrorReport(
    string ErrorReferenceId,
    ErrorCategory Category,
    string UserFriendlyMessage,
    string? CorrelationId,
    DiagnosisAction DiagnosisAction = DiagnosisAction.Unknown);
