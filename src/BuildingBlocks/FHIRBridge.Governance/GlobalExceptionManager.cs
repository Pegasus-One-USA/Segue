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

    public GlobalExceptionManager(IGovernanceLogger governanceLogger, IExceptionClassifier classifier)
    {
        _governanceLogger = governanceLogger;
        _classifier = classifier;
    }

    public async Task<ErrorReport> CaptureAsync(
        Exception exception, ExceptionContext context, CancellationToken cancellationToken = default)
    {
        var referenceId = ErrorReference.New();
        var category = _classifier.Classify(exception);
        var friendlyMessage = string.IsNullOrWhiteSpace(context.UserFriendlyMessageOverride)
            ? DefaultMessageFor(category)
            : context.UserFriendlyMessageOverride!;

        // Capturing an error must never itself throw — a failure here (e.g. DB unreachable) must not mask the
        // original exception or crash the host. Worst case the caller still gets a reference id to quote.
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
                    context.SpanId),
                cancellationToken);
        }
        catch
        {
            // Intentionally swallowed: see remarks above.
        }

        return new ErrorReport(referenceId, category, friendlyMessage, context.CorrelationId);
    }

    /// <summary>Category-specific, PHI/PII-free text safe to show any end user. The reference id is returned
    /// as a separate field on <see cref="ErrorReport"/> so the UI can present it on its own line.</summary>
    private static string DefaultMessageFor(ErrorCategory category) => category switch
    {
        ErrorCategory.Validation =>
            "The request could not be processed because some information was invalid. Please contact your system administrator and provide the reference ID below.",
        ErrorCategory.Authentication =>
            "We could not verify your identity for this request. Please contact your system administrator and provide the reference ID below.",
        ErrorCategory.Authorization =>
            "You do not have permission to perform this action. Please contact your system administrator and provide the reference ID below.",
        ErrorCategory.Network or ErrorCategory.ExternalSystem =>
            "A connected system did not respond as expected while processing your request. Please contact your system administrator and provide the reference ID below.",
        _ =>
            "An unexpected error occurred while processing your request. Please contact your system administrator and provide the reference ID below.",
    };
}
