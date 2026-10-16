using System.Diagnostics;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Entities.Governance;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Governance;
using FHIRBridge.SharedKernel.Observability;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Cds;

/// <summary>
/// The public CDS Hooks 2.0 endpoints. Each invoke/feedback call is authenticated by the client's JWT
/// (<see cref="CdsJwtValidator"/>), recorded as exactly one <see cref="CdsCallLog"/> row (metadata only) and one
/// metric; rejected calls also raise a governance security event. Nothing from the request body — context,
/// prefetch, fhirAuthorization, patient or user ids, card text, feedback comments — is ever logged or stored.
/// </summary>
public sealed class CdsHooksService : ICdsHooksService
{
    private const string KindInvoke = "invoke";
    private const string KindFeedback = "feedback";
    private const string UnknownServiceTag = "unknown";

    /// <summary>The SecurityEvent.Details column length; a longer value would fail the insert and lose the event.</summary>
    private const int SecurityEventDetailsMaxLength = 1000;
    private const int SecurityEventIssuerMaxLength = 200;

    private readonly ICdsServiceInstanceRepository _instances;
    private readonly ICdsServiceHandlerRegistry _handlers;
    private readonly ICdsCallLogRepository _callLog;
    private readonly ICdsPublicBaseUrlResolver _baseUrlResolver;
    private readonly CdsJwtValidator _jwtValidator;
    private readonly CdsEvaluator _evaluator;
    private readonly IGovernanceLogger _governanceLogger;
    private readonly ICdsMetrics _metrics;
    private readonly ILogger<CdsHooksService> _logger;

    public CdsHooksService(
        ICdsServiceInstanceRepository instances,
        ICdsServiceHandlerRegistry handlers,
        ICdsCallLogRepository callLog,
        ICdsPublicBaseUrlResolver baseUrlResolver,
        CdsJwtValidator jwtValidator,
        CdsEvaluator evaluator,
        IGovernanceLogger governanceLogger,
        ICdsMetrics metrics,
        ILogger<CdsHooksService> logger)
    {
        _instances = instances;
        _handlers = handlers;
        _callLog = callLog;
        _baseUrlResolver = baseUrlResolver;
        _jwtValidator = jwtValidator;
        _evaluator = evaluator;
        _governanceLogger = governanceLogger;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<CdsDiscoveryResponse> GetDiscoveryAsync(CancellationToken cancellationToken)
    {
        var instances = await _instances.GetEnabledAsync(cancellationToken);
        var services = new List<CdsServiceDefinition>();
        foreach (var instance in instances.OrderBy(i => i.ServiceId, StringComparer.Ordinal))
        {
            // An instance whose handler type is no longer registered cannot answer, so it is not advertised.
            if (_handlers.Find(instance.HandlerKey) is not { } handler)
            {
                continue;
            }

            var prefetch = handler.GetPrefetchTemplates(instance.Hook);
            services.Add(new CdsServiceDefinition
            {
                Hook = instance.Hook,
                Title = instance.Title,
                Description = instance.Description,
                Id = instance.ServiceId,
                Prefetch = prefetch.Count > 0 ? prefetch : null,
            });
        }

        return new CdsDiscoveryResponse { Services = services };
    }

    public async Task<CdsInvokeResult> InvokeAsync(
        string serviceId, CdsRequest? request, CdsCallerContext caller, CancellationToken cancellationToken)
    {
        var call = new CallRecord(CdsCallKind.Invoke, serviceId)
        {
            Hook = request?.Hook,
            HookInstance = request?.HookInstance,
            Metadata = CdsRequestMetadata.Read(request),
        };
        try
        {
            var gate = await AdmitAsync(call, caller, feedback: false, cancellationToken);
            if (gate is not null)
            {
                return new CdsInvokeResult(gate.Value.Status, null, gate.Value.Error);
            }

            var instance = call.Instance!;
            if (_handlers.Find(instance.HandlerKey) is not { } handler)
            {
                await FinishAsync(call, CdsCallOutcome.Error, CdsCallStatus.Error, 0, "The handler type of this service is not available.");
                return new CdsInvokeResult(CdsCallStatus.Error, null, "This CDS service is not available.");
            }

            var evaluation = await _evaluator.EvaluateAsync(instance, handler, request, call.TrustedIssuer, cancellationToken);
            call.CallbackResourceCount = evaluation.CallbackResourceCount;
            call.CallbackMs = evaluation.CallbackMs;
            call.SuggestionsOffered = evaluation.Response?.Cards.Sum(card => card.Suggestions?.Count ?? 0) ?? 0;
            call.SystemActionCount = evaluation.SystemActionCount;
            call.Coverage = evaluation.Coverage;
            call.CoverageAssertionCount = evaluation.CoverageAssertionCount;
            await FinishAsync(call, evaluation.Outcome, evaluation.Status, evaluation.CardCount, evaluation.Detail);
            return evaluation.Status == CdsCallStatus.Ok
                ? new CdsInvokeResult(CdsCallStatus.Ok, evaluation.Response ?? new CdsResponse())
                : new CdsInvokeResult(evaluation.Status, null, evaluation.Detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ClientClosedAsync(call);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("CDS invoke of {ServiceId} failed: {ErrorType}.", call.ServiceTag, exception.GetType().Name);
            await FinishAsync(call, CdsCallOutcome.Error, CdsCallStatus.Error, 0, $"Internal error ({exception.GetType().Name}).");
            return new CdsInvokeResult(CdsCallStatus.Error, null, "The CDS service failed.");
        }
    }

    public async Task<CdsFeedbackResult> SubmitFeedbackAsync(
        string serviceId, CdsFeedbackRequest? request, CdsCallerContext caller, CancellationToken cancellationToken)
    {
        var call = new CallRecord(CdsCallKind.Feedback, serviceId);
        try
        {
            var gate = await AdmitAsync(call, caller, feedback: true, cancellationToken);
            if (gate is not null)
            {
                return new CdsFeedbackResult(gate.Value.Status, gate.Value.Error);
            }

            if (request is null)
            {
                const string unreadable = "The request body is missing or is not valid JSON.";
                await FinishAsync(call, CdsCallOutcome.BadRequest, CdsCallStatus.BadRequest, 0, unreadable);
                return new CdsFeedbackResult(CdsCallStatus.BadRequest, unreadable);
            }

            var items = request.Feedback ?? [];
            if (items.Count == 0)
            {
                await FinishAsync(call, CdsCallOutcome.BadRequest, CdsCallStatus.BadRequest, 0, "The feedback list is empty.");
                return new CdsFeedbackResult(CdsCallStatus.BadRequest, "The feedback list is empty.");
            }

            if (items.Any(item => item is null || string.IsNullOrWhiteSpace(item.Card) || item.Outcome is not ("accepted" or "overridden")))
            {
                const string invalid = "Each feedback item needs a card uuid and an outcome of 'accepted' or 'overridden'.";
                await FinishAsync(call, CdsCallOutcome.BadRequest, CdsCallStatus.BadRequest, items.Count, invalid);
                return new CdsFeedbackResult(CdsCallStatus.BadRequest, invalid);
            }

            // Counts and coded override reasons only — never a userComment or a reason's display text. Epic may
            // batch several items, uses "overrideReasons" (plural) and, for a reason the health system did not map,
            // sends its own reasonCategory code in an extension (see CdsOverrideReason.RecordableCode).
            var accepted = items.Count(item => item.Outcome == "accepted");
            var itemReasons = items
                .Select(item => item.EffectiveOverrideReason?.RecordableCode())
                .OfType<string>()
                .ToArray();
            var reasons = itemReasons.Distinct(StringComparer.Ordinal).ToArray();
            call.SuggestionsAccepted = items.Sum(item =>
                item.AcceptedSuggestions?.Count(suggestion => !string.IsNullOrWhiteSpace(suggestion?.Id)) ?? 0);
            // A count per code ("will-monitor:5"), so batched feedback is counted per item in Activity.
            call.OverrideReasonCodes = CdsReasonCodeCounts.Format(itemReasons, CdsCallLog.OverrideReasonCodesMaxLength);
            var detail = $"Accepted: {accepted}, overridden: {items.Count - accepted}" +
                         (call.SuggestionsAccepted > 0 ? $", fixes accepted: {call.SuggestionsAccepted}" : string.Empty) +
                         (reasons.Length > 0 ? $" (reasons: {string.Join(", ", reasons.Take(5))})." : ".");
            await FinishAsync(call, CdsCallOutcome.Success, CdsCallStatus.Ok, items.Count, detail);
            return new CdsFeedbackResult(CdsCallStatus.Ok);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ClientClosedAsync(call);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("CDS feedback for {ServiceId} failed: {ErrorType}.", call.ServiceTag, exception.GetType().Name);
            await FinishAsync(call, CdsCallOutcome.Error, CdsCallStatus.Error, 0, $"Internal error ({exception.GetType().Name}).");
            return new CdsFeedbackResult(CdsCallStatus.Error, "The CDS service failed.");
        }
    }

    public Task RecordUnlicensedAsync(string serviceId, bool feedback) =>
        FinishAsync(
            new CallRecord(feedback ? CdsCallKind.Feedback : CdsCallKind.Invoke, serviceId),
            CdsCallOutcome.Error,
            CdsCallStatus.ServiceUnavailable,
            0,
            "Refused: Segue has no active license.");

    /// <summary>
    /// Finds the enabled instance, then authenticates the caller and checks the instance's issuer allow-list.
    /// Returns null when the call may proceed; otherwise the call has already been recorded.
    /// </summary>
    private async Task<(CdsCallStatus Status, string Error)?> AdmitAsync(
        CallRecord call, CdsCallerContext caller, bool feedback, CancellationToken cancellationToken)
    {
        var slug = call.RequestedServiceId.Trim().ToLowerInvariant();
        var instance = CdsServiceInstance.IsValidServiceId(slug)
            ? await _instances.GetByServiceIdAsync(slug, cancellationToken)
            : null;
        if (instance is null || !instance.IsEnabled)
        {
            await FinishAsync(call, CdsCallOutcome.NotFound, CdsCallStatus.NotFound, 0, "No enabled CDS service with this id.");
            return (CdsCallStatus.NotFound, "No CDS service with this id.");
        }

        call.Instance = instance;

        // The expected aud comes only from the configured Cds:PublicBaseUrl, never from the request's Host header:
        // otherwise a token signed for another CDS service could be replayed here with a matching Host. Unset, no
        // audience is expected and only an issuer with its own audience override can pass (fail closed).
        var baseUrl = await _baseUrlResolver.ResolveAsync(caller.RequestOrigin, cancellationToken);
        string[] audiences = !baseUrl.IsConfigured
            ? []
            : feedback
                ? [baseUrl.ServiceUrl(instance.ServiceId), baseUrl.FeedbackUrl(instance.ServiceId)]
                : [baseUrl.ServiceUrl(instance.ServiceId)];
        if (!baseUrl.IsConfigured)
        {
            _logger.LogWarning("CDS call to {ServiceId}: {ConfigurationKey} is not set, so only issuers with an audience override are accepted.",
                instance.ServiceId, CdsPublicBaseUrl.ConfigurationKey);
        }

        var auth = await _jwtValidator.ValidateAsync(caller.AuthorizationHeader, audiences, cancellationToken);
        call.Issuer = auth.Issuer;
        call.TrustedIssuerId = auth.TrustedIssuer?.Id;
        call.TrustedIssuer = auth.IsValid ? auth.TrustedIssuer : null;
        if (!auth.IsValid)
        {
            await FinishAsync(call, CdsCallOutcome.Unauthorized, CdsCallStatus.Unauthorized, 0, auth.Failure);
            await SecurityEventAsync(call, $"rejected (401): {auth.Failure}");
            return (CdsCallStatus.Unauthorized, "The caller could not be authenticated.");
        }

        if (!instance.AllowsIssuer(auth.TrustedIssuer!.Id))
        {
            const string notAllowed = "The issuer is not on this service's allow-list.";
            await FinishAsync(call, CdsCallOutcome.Forbidden, CdsCallStatus.Forbidden, 0, notAllowed);
            await SecurityEventAsync(call, $"refused (403): {notAllowed}");
            return (CdsCallStatus.Forbidden, "This client may not call this CDS service.");
        }

        return null;
    }

    /// <summary>The EHR hung up (or the request was aborted) before the call finished; it still leaves its audit row.</summary>
    private Task ClientClosedAsync(CallRecord call) =>
        FinishAsync(call, CdsCallOutcome.Error, CdsCallStatus.ClientClosedRequest, 0, "The client closed the connection before the call finished.");

    /// <summary>
    /// Writes the call's single log row and metric — at most once per call, so a failure path that runs after
    /// a first attempt never writes a second row. A failure here is logged, never surfaced to the caller.
    /// </summary>
    private async Task FinishAsync(CallRecord call, CdsCallOutcome outcome, CdsCallStatus status, int cardCount, string? detail)
    {
        if (call.Finished)
        {
            return;
        }

        call.Finished = true;
        var durationMs = call.Stopwatch.ElapsedMilliseconds;
        var hook = call.Instance?.Hook ?? Truncate(call.Hook, CdsCallLog.HookMaxLength);
        try
        {
            _metrics.RecordCall(new CdsCallMetric(
                call.Kind == CdsCallKind.Invoke ? KindInvoke : KindFeedback,
                call.Instance?.Hook,
                call.ServiceTag,
                outcome.ToString(),
                (int)status,
                cardCount,
                durationMs));

            // Not the request's cancellation token: a client that hangs up must still leave its audit row.
            await _callLog.AddAsync(
                new CdsCallLog(
                    DateTime.UtcNow,
                    call.Kind,
                    Truncate(call.LoggedServiceId, CdsCallLog.ServiceIdMaxLength) ?? string.Empty,
                    call.Instance?.Id,
                    call.Instance?.HandlerKey,
                    hook,
                    Truncate(call.HookInstance, CdsCallLog.HookInstanceMaxLength),
                    call.Issuer,
                    call.TrustedIssuerId,
                    outcome,
                    (int)status,
                    cardCount,
                    durationMs,
                    detail,
                    new CdsCallLogExtras(
                        call.Metadata.TriggerAction,
                        call.Metadata.EpicVersion,
                        call.Metadata.CriteriaId,
                        call.Metadata.ImplementationVersion,
                        call.CallbackResourceCount,
                        call.CallbackMs,
                        call.SuggestionsOffered,
                        call.SuggestionsAccepted,
                        call.OverrideReasonCodes,
                        call.SystemActionCount,
                        call.Coverage?.PayerId,
                        call.Coverage?.PayerName,
                        call.Coverage?.Decisions,
                        call.Coverage?.RelayUsed,
                        call.Coverage?.RelayMs,
                        call.Coverage?.RelayOutcome,
                        call.CoverageAssertionCount)),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogError("Could not record the CDS call log for {ServiceId}: {ErrorType}.", call.ServiceTag, exception.GetType().Name);
        }
    }

    private async Task SecurityEventAsync(CallRecord call, string what)
    {
        try
        {
            // iss and the failure text come from an unauthenticated token: bounded and stripped of control
            // characters so they can neither overflow the Details column (losing the event) nor forge log lines.
            var details = $"CDS {(call.Kind == CdsCallKind.Invoke ? KindInvoke : KindFeedback)} of service " +
                          $"'{call.ServiceTag}' {Clean(what, 600)} Issuer: {Clean(call.Issuer, SecurityEventIssuerMaxLength) ?? "unknown"}.";
            await _governanceLogger.LogSecurityEventAsync(
                new SecurityEventEntry(
                    EventType: "CdsCallRejected",
                    Severity: "Warning",
                    Details: Truncate(details, SecurityEventDetailsMaxLength)!),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogError("Could not record the CDS security event for {ServiceId}: {ErrorType}.", call.ServiceTag, exception.GetType().Name);
        }
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null ? null : value.Length > maxLength ? value[..maxLength] : value;

    /// <summary>Control characters replaced by spaces, then cut to <paramref name="maxLength"/>.</summary>
    private static string? Clean(string? value, int maxLength) =>
        value is null ? null : Truncate(new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray()), maxLength);

    /// <summary>What is known about one call so far; becomes its CdsCallLog row.</summary>
    private sealed class CallRecord
    {
        public CallRecord(CdsCallKind kind, string? requestedServiceId)
        {
            Kind = kind;
            RequestedServiceId = requestedServiceId ?? string.Empty;
        }

        public CdsCallKind Kind { get; }

        public string RequestedServiceId { get; }

        public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();

        public CdsServiceInstance? Instance { get; set; }

        public string? Hook { get; init; }

        public string? HookInstance { get; init; }

        public string? Issuer { get; set; }

        public Guid? TrustedIssuerId { get; set; }

        /// <summary>The authenticated caller's trusted issuer (decides whether a FHIR callback may run).</summary>
        public CdsTrustedIssuer? TrustedIssuer { get; set; }

        /// <summary>Set once the log row has been attempted, so no path writes a second one.</summary>
        public bool Finished { get; set; }

        /// <summary>The request's Epic extension values (short tokens only; invoke).</summary>
        public CdsRequestMetadata Metadata { get; init; } = CdsRequestMetadata.Empty;

        /// <summary>Resources fetched from the client's fhirServer for missing prefetch; null when no callback ran.</summary>
        public int? CallbackResourceCount { get; set; }

        public long? CallbackMs { get; set; }

        /// <summary>Invoke: suggestions offered across the returned cards.</summary>
        public int SuggestionsOffered { get; set; }

        /// <summary>Feedback: acceptedSuggestions ids received.</summary>
        public int SuggestionsAccepted { get; set; }

        /// <summary>Feedback: distinct override reason codes, comma-separated (codes only).</summary>
        public string? OverrideReasonCodes { get; set; }

        /// <summary>Invoke: systemActions returned (coverage requirements only).</summary>
        public int SystemActionCount { get; set; }

        /// <summary>Invoke, coverage requirements: payer, decision codes and relay facts (no patient data).</summary>
        public CdsCoverageCallInfo? Coverage { get; set; }

        public int? CoverageAssertionCount { get; set; }

        /// <summary>
        /// The service id to log: the matched instance's own id, otherwise the requested id trimmed and lower-cased
        /// (how lookup and the Activity filter compare it), so Activity filtering finds every call to a service.
        /// </summary>
        public string LoggedServiceId => Instance?.ServiceId ?? RequestedServiceId.Trim().ToLowerInvariant();

        /// <summary>The metric/log tag: the instance's service id, or a fixed value so unknown ids cannot explode cardinality.</summary>
        public string ServiceTag => Instance?.ServiceId ?? UnknownServiceTag;
    }
}
