using FHIRBridge.Observability.Logging;
using Microsoft.Extensions.Logging;

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
    private readonly IPhiRedactor _redactor;
    private readonly IErrorScrubber _scrubber;
    private readonly IErrorSinkRouter? _sinkRouter;
    private readonly IErrorCapturePolicy? _policy;
    private readonly ILogger<GlobalExceptionManager>? _logger;
    private readonly IErrorWriteQueue? _writeQueue;
    private readonly ICorrelationIdAccessor? _correlation;

    public GlobalExceptionManager(
        IGovernanceLogger governanceLogger,
        IExceptionClassifier classifier,
        IFailureDiagnosisClassifier? diagnosisClassifier = null,
        IPhiRedactor? redactor = null,
        IErrorScrubber? scrubber = null,
        IErrorSinkRouter? sinkRouter = null,
        IErrorCapturePolicy? policy = null,
        ILogger<GlobalExceptionManager>? logger = null,
        IErrorWriteQueue? writeQueue = null,
        ICorrelationIdAccessor? correlation = null)
    {
        _writeQueue = writeQueue;
        _correlation = correlation;
        _policy = policy;
        _logger = logger;
        _governanceLogger = governanceLogger;
        _classifier = classifier;
        _diagnosisClassifier = diagnosisClassifier ?? new DefaultFailureDiagnosisClassifier();
        // HIPAA #10: same redaction rule set as the Serilog PhiMaskingEnricher — see its remarks.
        _redactor = redactor ?? new PhiRedactor();
        // Central scrubber + configurable sinks (table / Application Insights / both). Both optional: when the host
        // has not called AddFhirBridgeErrorCapture, behaviour is the original table-only capture.
        _scrubber = scrubber ?? new ErrorScrubber(_redactor);
        _sinkRouter = sinkRouter;
    }

    private Task WriteAsync(ErrorEntry entry, CancellationToken cancellationToken)
    {
        if (_writeQueue is not null)
        {
            // Recording an error never makes the caller wait: the entry is handed to the background writer (which retries
            // while the table is busy or locked) and the caller carries on. What the writer cannot read later - the
            // request's correlation id and the time it happened - is fixed now.
            var queued = entry with
            {
                CorrelationId = entry.CorrelationId ?? _correlation?.CorrelationId,
                OccurredUtc = entry.OccurredUtc ?? DateTime.UtcNow,
            };
            return _writeQueue.TryEnqueue(queued)
                ? Task.CompletedTask
                : Task.FromException(new InvalidOperationException("The error write queue is full."));
        }

        return _sinkRouter is null
            ? _governanceLogger.LogErrorAsync(entry, cancellationToken)
            : _sinkRouter.WriteAsync(entry, cancellationToken);
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

        // Tell the ambient catch-all this failure is handled, so it is never recorded a second time.
        CapturedExceptionRegistry.Mark(exception);

        // Error-log settings: an admin can switch off whole severities / categories. A skipped error is simply not
        // recorded (no reference id is handed out - see ErrorReport).
        if (_policy is not null && !_policy.Current.ShouldCapture(context.Severity, category.ToString()))
        {
            return new ErrorReport(null, category, friendlyMessage, context.CorrelationId, diagnosis.Action);
        }
        var where = ErrorContext.For(exception);
        string? Name(string? explicitValue, string? ambient)
        {
            var value = string.IsNullOrWhiteSpace(explicitValue) ? ambient : explicitValue;
            if (string.IsNullOrWhiteSpace(value)) return null;
            var clean = _scrubber.ScrubText(value);
            return clean.Length <= 200 ? clean : clean[..200];
        }

        var workflowName = Name(context.WorkflowName, where?.WorkflowName);
        var nodeName = Name(context.NodeName, where?.NodeName);
        var nodeType = Name(context.NodeType, where?.NodeType);
        var sourceName = Name(context.SourceName, where?.SourceName);
        var destinationName = Name(context.DestinationName, where?.DestinationName);
        var resourceType = Name(context.ResourceType, where?.ResourceType);

        ScrubbedError scrubbed;
        try
        {
            scrubbed = _scrubber.Scrub(exception);
        }
        catch
        {
            // Fail closed: if scrubbing itself breaks, record that the error happened but none of its text.
            const string withheld = "[withheld: scrubbing failed]";
            scrubbed = new ScrubbedError(exception.GetType().Name, withheld, withheld, exception.GetType().Name, withheld);
        }

        var referenceId = ErrorReference.New();
        var persisted = false;
        ErrorEntry? lastEntry = null;

        // Capturing an error must never itself throw — a failure here (e.g. DB unreachable) must not mask the
        // original exception or crash the host. On failure, a fresh reference id is tried again (see remarks on
        // ErrorReference) rather than giving up after the first attempt — otherwise a same-day restart's first
        // handful of captures would silently vanish instead of ending up in ErrorLogs. If every attempt still
        // fails to persist, the returned report's ErrorReferenceId is null — a caller must never hand out an id
        // that has no matching ErrorLogs row for Operations → Errors (or the caller) to ever find.
        for (var attempt = 1; attempt <= MaxCaptureAttempts; attempt++)
        {
            try
            {
                lastEntry = new ErrorEntry(
                        context.Severity,
                        scrubbed.ExceptionType,
                        scrubbed.Message,
                        scrubbed.Detail,
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
                        diagnosis.Cause,
                        workflowName,
                        nodeName,
                        nodeType,
                        sourceName,
                        destinationName,
                        resourceType);
                await WriteAsync(lastEntry, cancellationToken);

                persisted = true;
                break;
            }
            catch (Exception caught)
            {
                if (attempt == MaxCaptureAttempts)
                {
                    // Intentionally swallowed (see remarks above) - but never silently: say so in the application log.
                    _logger?.LogWarning(
                        caught, "Could not record an error entry ({ExceptionType}); it was not written to the error log.",
                        exception.GetType().Name);
                    break;
                }

                referenceId = ErrorReference.New();
            }
        }

        if (!persisted && lastEntry is not null && _sinkRouter is not null)
        {
            // Table unreachable on every attempt: if Application Insights is configured it still receives the error.
            try
            {
                persisted = await _sinkRouter.WriteFallbackAsync(lastEntry with { ErrorReferenceId = referenceId }, cancellationToken);
            }
            catch
            {
                // Intentionally swallowed: see remarks above.
            }
        }

        return new ErrorReport(persisted ? referenceId : null, category, friendlyMessage, context.CorrelationId, diagnosis.Action);
    }

    public async Task<string?> CaptureTraceAsync(
        string severity, string entryType, string message, ExceptionContext context,
        DateTime? occurredUtc = null, CancellationToken cancellationToken = default)
    {
        if (_policy is not null && !_policy.Current.ShouldCapture(severity, null))
        {
            return null;
        }

        string? Name(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var clean = _scrubber.ScrubText(value);
            return clean.Length <= 200 ? clean : clean[..200];
        }

        var referenceId = ErrorReference.New();
        for (var attempt = 1; attempt <= MaxCaptureAttempts; attempt++)
        {
            try
            {
                await WriteAsync(
                    new ErrorEntry(
                        severity,
                        entryType,
                        _scrubber.ScrubText(message),
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
                        DiagnosisCause: null,
                        Name(context.WorkflowName),
                        Name(context.NodeName),
                        Name(context.NodeType),
                        Name(context.SourceName),
                        Name(context.DestinationName),
                        Name(context.ResourceType),
                        occurredUtc),
                    cancellationToken);
                return referenceId;
            }
            catch (Exception caught)
            {
                if (attempt == MaxCaptureAttempts)
                {
                    // Same never-throw guarantee as the other capture paths: a trace line must not fail the run it
                    // describes. But say why it was lost - most often the ErrorLogs table is missing a column
                    // (database migrations not applied) or the database is unreachable.
                    _logger?.LogWarning(caught, "Could not record a {Severity} entry in the error log.", severity);
                    return null;
                }

                // A reference id minted before a same-day restart can collide with one already stored: try a fresh one.
                referenceId = ErrorReference.New();
            }
        }

        return null;
    }

    public async Task<string?> CaptureExpectedAsync(
        ExpectedFailure failure, ExceptionContext context, CancellationToken cancellationToken = default)
    {
        var referenceId = ErrorReference.New();

        // Same never-throw guarantee as CaptureAsync: capturing must not itself fail the request. Unlike
        // CaptureAsync there's no retry-on-failure here (this path is best-effort/Informational already) —
        // a single failed write simply means no reference id is returned, same "never hand out an orphaned
        // id" guarantee.
        try
        {
            await WriteAsync(
                new ErrorEntry(
                    "Informational",
                    failure.ExceptionType,
                    _scrubber.ScrubText(failure.Message),
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
            return null;
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
