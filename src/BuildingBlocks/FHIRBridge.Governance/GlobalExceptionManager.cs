namespace FHIRBridge.Governance;

/// <summary>
/// Default <see cref="IGlobalExceptionManager"/>. Cloud-agnostic and dependency-light: it only needs the
/// existing <see cref="IGovernanceLogger"/> (which writes to <c>ErrorLogs</c>) and an
/// <see cref="IExceptionClassifier"/>. This is the "additional cross-cutting layer" from the Phase 6A
/// design — it sits on top of the governance logger, it does not replace it.
/// </summary>
public sealed class GlobalExceptionManager : IGlobalExceptionManager
{
    private readonly IGovernanceLogger _governanceLogger;
    private readonly IExceptionClassifier _classifier;
    private readonly IFailureDiagnosisClassifier _diagnosisClassifier;

    public GlobalExceptionManager(
        IGovernanceLogger governanceLogger,
        IExceptionClassifier classifier,
        IFailureDiagnosisClassifier? diagnosisClassifier = null)
    {
        _governanceLogger = governanceLogger;
        _classifier = classifier;
        _diagnosisClassifier = diagnosisClassifier ?? new DefaultFailureDiagnosisClassifier();
    }

    // Bounds retry-on-collision below: a same-day process restart can regenerate a reference id that collides
    // with one persisted by a prior instance (see ErrorReference's remarks) — a handful of attempts is enough to
    // clear that without risking a real, non-collision persistence failure (e.g. DB unreachable) looping pointlessly.
    private const int MaxCaptureAttempts = 3;

    public async Task<ErrorReport> CaptureAsync(
        Exception exception, ExceptionContext context, CancellationToken cancellationToken = default)
    {
        var category = _classifier.Classify(exception);
        var diagnosis = _diagnosisClassifier.Diagnose(exception, category);
        var friendlyMessage = string.IsNullOrWhiteSpace(context.UserFriendlyMessageOverride)
            ? DefaultMessageFor(category, diagnosis)
            : context.UserFriendlyMessageOverride!;

        var referenceId = ErrorReference.New();

        // Capturing an error must never itself throw — a failure here (e.g. DB unreachable) must not mask the
        // original exception or crash the host. Worst case the caller still gets a reference id to quote, even if
        // every attempt below failed to persist. On failure, a fresh reference id is tried again (see remarks on
        // ErrorReference) rather than giving up after the first attempt — otherwise a same-day restart's first
        // handful of captures would silently vanish instead of ending up in ErrorLogs.
        for (var attempt = 1; attempt <= MaxCaptureAttempts; attempt++)
        {
            try
            {
                await _governanceLogger.LogErrorAsync(
                    new ErrorEntry(
                        context.Severity,
                        exception.GetType().Name,
                        exception.Message,
                        exception.ToString(),
                        context.Module,
                        context.CorrelationId,
                        referenceId,
                        category.ToString(),
                        friendlyMessage,
                        context.ExecutionId,
                        context.WorkflowId,
                        context.EndpointId,
                        context.RequestId,
                        context.TraceId,
                        context.SpanId,
                        diagnosis.Action,
                        diagnosis.Cause),
                    cancellationToken);

                break;
            }
            catch
            {
                if (attempt == MaxCaptureAttempts)
                {
                    // Intentionally swallowed: see remarks above.
                    break;
                }

                referenceId = ErrorReference.New();
            }
        }

        return new ErrorReport(referenceId, category, friendlyMessage, context.CorrelationId, diagnosis.Action);
    }

    public async Task<string> CaptureExpectedAsync(
        ExpectedFailure failure, ExceptionContext context, CancellationToken cancellationToken = default)
    {
        var referenceId = ErrorReference.New();

        // Same never-throw guarantee as CaptureAsync: capturing must not itself fail the request.
        try
        {
            await _governanceLogger.LogErrorAsync(
                new ErrorEntry(
                    "Informational",
                    failure.ExceptionType,
                    failure.Message,
                    StackTrace: null,
                    context.Module,
                    context.CorrelationId,
                    referenceId,
                    Category: null,
                    UserFriendlyMessage: null,
                    context.ExecutionId,
                    context.WorkflowId,
                    context.EndpointId,
                    context.RequestId,
                    context.TraceId,
                    context.SpanId,
                    DiagnosisAction: null,
                    DiagnosisCause: null),
                cancellationToken);
        }
        catch
        {
            // Intentionally swallowed: see remarks above.
        }

        return referenceId;
    }

    /// <summary>Category-specific, PHI/PII-free text safe to show any end user. The reference id is returned
    /// as a separate field on <see cref="ErrorReport"/> so the UI can present it on its own line.
    /// <para>The closing clause is driven by <paramref name="diagnosis"/>'s <see cref="DiagnosisAction"/> — not
    /// hardcoded to "contact your system administrator" for every category as before — so this message and any
    /// UI badge sourced from the same <see cref="Diagnosis"/> can never disagree
    /// (docs/ERRORS_SCREEN_CATEGORIZATION_ANALYSIS.md §1, §8).</para></summary>
    private static string DefaultMessageFor(ErrorCategory category, Diagnosis diagnosis)
    {
        var lead = category switch
        {
            ErrorCategory.Validation =>
                "The request could not be processed because some information was invalid.",
            ErrorCategory.Authentication =>
                "We could not verify your identity for this request.",
            ErrorCategory.Authorization =>
                "You do not have permission to perform this action.",
            ErrorCategory.Network or ErrorCategory.ExternalSystem =>
                "A connected system did not respond as expected while processing your request.",
            _ =>
                "An unexpected error occurred while processing your request.",
        };

        var closing = diagnosis.Action switch
        {
            DiagnosisAction.SelfFix => diagnosis.Cause,
            DiagnosisAction.ContactSupport =>
                "Please contact your system administrator and provide the reference ID below.",
            _ =>
                "Please contact your system administrator and provide the reference ID below.",
        };

        return $"{lead} {closing}";
    }
}
