using System.Diagnostics;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Cds.Handlers;
using FHIRBridge.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Infrastructure.Cds;

/// <summary>What one evaluation produced; shared by the live endpoint and the admin Test screen.</summary>
/// <param name="Detail">A short, non-PHI reason for the call log (null on a plain success).</param>
/// <param name="Messages">Notes for the Test screen (same non-PHI content as <paramref name="Detail"/>).</param>
/// <param name="CallbackResourceCount">Resources fetched from the client's fhirServer; null when no callback ran.</param>
/// <param name="CallbackMs">Wall time of the callback; null when no callback ran.</param>
public sealed record CdsEvaluation(
    CdsCallOutcome Outcome,
    CdsCallStatus Status,
    CdsResponse? Response,
    string? Detail,
    IReadOnlyList<string> Messages,
    long DurationMs,
    JsonObject ResolvedSettings,
    int? CallbackResourceCount = null,
    long? CallbackMs = null)
{
    public int CardCount => Response?.Cards.Count ?? 0;
}

/// <summary>
/// The one evaluation path behind both POST /cds-services/{id} and the admin Test screen: checks the request
/// against the instance (hook, context), applies the CDS Hooks 2.0 prefetch rule, runs the handler inside the
/// configured time budget, and cleans up the cards it returns. Never logs or keeps anything from the request.
/// </summary>
public sealed class CdsEvaluator
{
    /// <summary>Upper bound on cards returned per call, so a misbehaving rule cannot flood the EHR.</summary>
    public const int MaxCards = 25;

    /// <summary>Upper bound on suggestions per card.</summary>
    public const int MaxSuggestionsPerCard = 10;

    private readonly ISystemSettingsCache _settingsCache;
    private readonly IOptionsMonitor<CdsOptions> _options;
    private readonly ILogger<CdsEvaluator> _logger;
    private readonly ICdsFhirCallbackClient? _callbackClient;

    /// <param name="callbackClient">Fetches missing prefetch from the client's fhirServer; null disables the callback.</param>
    public CdsEvaluator(
        ISystemSettingsCache settingsCache,
        IOptionsMonitor<CdsOptions> options,
        ILogger<CdsEvaluator> logger,
        ICdsFhirCallbackClient? callbackClient = null)
    {
        _settingsCache = settingsCache;
        _options = options;
        _logger = logger;
        _callbackClient = callbackClient;
    }

    /// <summary>Evaluates with no authenticated caller (the admin Test run): missing prefetch is never fetched.</summary>
    public Task<CdsEvaluation> EvaluateAsync(
        CdsServiceInstance instance,
        ICdsServiceHandler handler,
        CdsRequest? request,
        CancellationToken cancellationToken) =>
        EvaluateAsync(instance, handler, request, null, cancellationToken);

    /// <param name="trustedIssuer">
    /// The authenticated caller. Null for the admin Test run, which therefore never calls back to a FHIR server.
    /// </param>
    public async Task<CdsEvaluation> EvaluateAsync(
        CdsServiceInstance instance,
        ICdsServiceHandler handler,
        CdsRequest? request,
        CdsTrustedIssuer? trustedIssuer,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var settings = CdsSettingsValues.Resolve(handler.Settings, instance.SettingsJson);
        var resolvedSettings = settings.ToJsonObject();
        int? callbackResourceCount = null;
        long? callbackMs = null;

        CdsEvaluation Finish(CdsCallOutcome outcome, CdsCallStatus status, CdsResponse? response, string? detail) =>
            new(outcome, status, response, detail, detail is null ? [] : [detail], stopwatch.ElapsedMilliseconds, resolvedSettings,
                callbackResourceCount, callbackMs);

        if (request is null)
        {
            return Finish(CdsCallOutcome.BadRequest, CdsCallStatus.BadRequest, null, "The request body is missing or is not valid JSON.");
        }

        if (string.IsNullOrWhiteSpace(request.Hook))
        {
            return Finish(CdsCallOutcome.BadRequest, CdsCallStatus.BadRequest, null, "The request has no hook.");
        }

        if (!string.Equals(request.Hook, instance.Hook, StringComparison.Ordinal))
        {
            return Finish(CdsCallOutcome.BadRequest, CdsCallStatus.BadRequest, null,
                $"This service answers the '{instance.Hook}' hook, not '{Truncate(request.Hook, 60)}'.");
        }

        if (string.IsNullOrWhiteSpace(request.HookInstance))
        {
            return Finish(CdsCallOutcome.BadRequest, CdsCallStatus.BadRequest, null, "The request has no hookInstance.");
        }

        if (request.Context is null)
        {
            return Finish(CdsCallOutcome.BadRequest, CdsCallStatus.BadRequest, null, "The request has no context.");
        }

        // CDS Hooks 2.0 prefetch rule: no fhirServer to fetch missing prefetch from → 412. With a fhirServer, the
        // missing keys are fetched from it (the callback, when the caller and the service allow it, inside the call's
        // time budget); whatever is still missing afterwards — or was sent as null, meaning the client could not
        // fetch it — is answered with no cards (Degraded).
        var required = handler.GetPrefetchTemplates(instance.Hook).Keys.ToArray();
        var absent = Absent(required, request.Prefetch);
        if (absent.Length > 0 && string.IsNullOrWhiteSpace(request.FhirServer))
        {
            return Finish(CdsCallOutcome.PreconditionFailed, CdsCallStatus.PreconditionFailed, null,
                $"Missing prefetch: {string.Join(", ", absent)} (and no fhirServer to fetch it from).");
        }

        var timeoutMs = Math.Clamp(
            await _settingsCache.GetIntAsync(CdsOptions.TimeoutMsKey, _options.CurrentValue.TimeoutMs, cancellationToken),
            100,
            30_000);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeoutMs);

        string? callbackNote = null;
        if (absent.Length > 0)
        {
            var callback = await FetchMissingAsync(instance, handler, request, trustedIssuer, absent, timeoutMs, budget.Token);
            cancellationToken.ThrowIfCancellationRequested();
            callbackNote = callback.Note;
            if (callback.ElapsedMs is { } elapsed)
            {
                // A callback ran (even one cut off by the budget): record it so Activity shows it.
                callbackResourceCount = callback.Result?.ResourceCount ?? 0;
                callbackMs = callback.Result?.DurationMs ?? elapsed;
            }

            if (budget.IsCancellationRequested)
            {
                return Finish(CdsCallOutcome.Degraded, CdsCallStatus.Ok, new CdsResponse(),
                    $"Fetching missing data used the whole {timeoutMs} ms time budget" +
                    (callbackNote is null ? string.Empty : $" ({callbackNote})") +
                    ", so the rules did not run. Answered with no cards.");
            }

            if (callback.Result is { } fetched)
            {
                request.Prefetch ??= new JsonObject();
                foreach (var (key, resource) in fetched.Resources)
                {
                    if (resource is not null && !request.Prefetch.ContainsKey(key))
                    {
                        request.Prefetch[key] = resource.Parent is null ? resource : resource.DeepClone();
                    }
                }
            }

            absent = Absent(required, request.Prefetch);
        }

        var unavailable = required.Where(key => request.Prefetch?.ContainsKey(key) == true && request.Prefetch[key] is null).ToArray();
        if (absent.Length > 0 || unavailable.Length > 0)
        {
            return Finish(CdsCallOutcome.Degraded, CdsCallStatus.Ok, new CdsResponse(),
                Truncate($"Prefetch not available: {string.Join(", ", absent.Concat(unavailable))}" +
                         (callbackNote is null ? string.Empty : $" ({callbackNote})") + ". Answered with no cards.", 500));
        }

        using var activity = FhirBridgeCdsMetrics.ActivitySource.StartActivity("cds.evaluate");
        activity?.SetTag("cds.hook", instance.Hook);
        activity?.SetTag("cds.service_id", instance.ServiceId);
        activity?.SetTag("cds.handler", handler.Key);

        CdsHandlerResult result;
        try
        {
            var context = new CdsHandlerContext(CdsInstanceSnapshot.From(instance), request, settings);
            // Off the request thread so the budget holds even for a handler that does its work synchronously.
            result = await Task.Run(() => handler.EvaluateAsync(context, budget.Token), budget.Token).WaitAsync(budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "timeout");
            return Finish(CdsCallOutcome.Degraded, CdsCallStatus.Ok, new CdsResponse(),
                $"The rules did not finish within {timeoutMs} ms. Answered with no cards.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The exception type only: a message could quote request content.
            _logger.LogWarning(
                "CDS handler {HandlerKey} failed for service {ServiceId}: {ErrorType}.",
                handler.Key, instance.ServiceId, exception.GetType().Name);
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            return Finish(CdsCallOutcome.Error, CdsCallStatus.Error, null, $"The rules failed ({exception.GetType().Name}).");
        }

        var cards = Sanitize(result.Cards, instance, request, out var droppedSuggestions);
        activity?.SetTag("cds.card_count", cards.Count);
        if (droppedSuggestions > 0)
        {
            // Counts only: a suggestion holds request data.
            _logger.LogWarning(
                "CDS handler {HandlerKey} for service {ServiceId} produced {Dropped} suggestion(s) that broke the suggestion rules; they were not sent.",
                handler.Key, instance.ServiceId, droppedSuggestions);
        }

        var response = new CdsResponse { Cards = cards };
        if (result.DegradedReason is { } degraded)
        {
            return Finish(CdsCallOutcome.Degraded, CdsCallStatus.Ok, response, Truncate(degraded, 300));
        }

        return cards.Count > 0
            ? Finish(CdsCallOutcome.Success, CdsCallStatus.Ok, response, null)
            : Finish(CdsCallOutcome.NoCards, CdsCallStatus.Ok, response, null);
    }

    /// <summary>
    /// Enforces the card contract whatever the handler did: non-empty summary under 140 characters, a valid
    /// indicator, a uuid, the instance's source label/url, and suggestions only when the instance's fixes are on and
    /// each one meets <see cref="CdsSuggestionRules"/> (one action; a delete names a draft order of this request; a
    /// created order is a draft). <c>selectionBehavior</c> is "any" exactly when suggestions remain.
    /// </summary>
    private static IReadOnlyList<CdsCard> Sanitize(
        IReadOnlyList<CdsCard>? cards,
        CdsServiceInstance instance,
        CdsRequest request,
        out int droppedSuggestions)
    {
        droppedSuggestions = 0;
        if (cards is null)
        {
            return [];
        }

        var fixesEnabled = instance.FixesEnabled;
        IReadOnlySet<string>? draftOrderReferences = null;
        var recommendedDeletes = new HashSet<string>(StringComparer.Ordinal);
        var clean = new List<CdsCard>();
        foreach (var card in cards)
        {
            var summary = card?.Summary?.Trim();
            if (card is null || string.IsNullOrEmpty(summary))
            {
                continue;
            }

            card.Summary = CdsCard.TruncateSummary(summary);
            card.Indicator = CdsIndicator.All.Contains(card.Indicator) ? card.Indicator : CdsIndicator.Info;
            card.Uuid = string.IsNullOrWhiteSpace(card.Uuid) ? Guid.NewGuid().ToString() : card.Uuid;
            card.Source = new CdsSource
            {
                Label = instance.CardSourceLabel,
                Url = instance.CardSourceUrl,
                Icon = card.Source?.Icon,
                Topic = card.Source?.Topic,
            };

            var kept = new List<CdsSuggestion>();
            foreach (var suggestion in card.Suggestions ?? [])
            {
                if (suggestion is null)
                {
                    continue;
                }

                draftOrderReferences ??= DraftOrderReferences(request);
                if (!fixesEnabled
                    || kept.Count == MaxSuggestionsPerCard
                    || CdsSuggestionRules.Validate(suggestion, draftOrderReferences) is not null)
                {
                    droppedSuggestions++;
                    continue;
                }

                if (suggestion.IsRecommended == true)
                {
                    // Epic pre-selects every recommended suggestion: at most one per card, and never a second
                    // recommended delete of a draft order an earlier card already recommends removing.
                    var deleteTarget = suggestion.Actions![0].Type == CdsActionTypes.Delete ? suggestion.Actions[0].ResourceId : null;
                    if (kept.Any(k => k.IsRecommended == true)
                        || (deleteTarget is not null && !recommendedDeletes.Add(deleteTarget)))
                    {
                        suggestion.IsRecommended = null;
                    }
                }

                kept.Add(suggestion);
            }

            card.Suggestions = kept.Count > 0 ? kept : null;
            card.SelectionBehavior = kept.Count > 0 ? CdsSelectionBehavior.Any : null;
            clean.Add(card);
            if (clean.Count == MaxCards)
            {
                break;
            }
        }

        return clean;
    }

    /// <summary>"Type/id" of every order in the request's context.draftOrders (what a delete action may name).</summary>
    private static IReadOnlySet<string> DraftOrderReferences(CdsRequest request) =>
        CdsFhirReader.Resources(request.Context?["draftOrders"])
            .Select(resource => (Type: CdsFhirReader.ResourceType(resource), Id: CdsFhirReader.Text(resource["id"])))
            .Where(order => CdsFhirReader.IsOrderType(order.Type) && !string.IsNullOrEmpty(order.Id))
            .Select(order => $"{order.Type}/{order.Id}")
            .ToHashSet(StringComparer.Ordinal);

    private static string[] Absent(IEnumerable<string> required, JsonObject? prefetch) =>
        required.Where(key => prefetch is null || !prefetch.ContainsKey(key)).ToArray();

    /// <summary>
    /// The FHIR callback (CDS Hooks 2.0 "FHIR resource access"): fetches the absent prefetch keys from the request's
    /// own fhirServer with its own fhirAuthorization token, all keys in parallel within Cds:CallbackTimeoutMs. Runs
    /// only for an authenticated caller whose trusted issuer allows callbacks, on a service with "Fetch missing data"
    /// on, and when fhirServer is that issuer's FHIR base. Never throws for a remote failure; the note is non-PHI.
    /// </summary>
    /// <returns>The fetched data, a non-PHI note, and the callback's wall time (null when no callback was attempted).</returns>
    private async Task<(CdsCallbackResult? Result, string? Note, long? ElapsedMs)> FetchMissingAsync(
        CdsServiceInstance instance,
        ICdsServiceHandler handler,
        CdsRequest request,
        CdsTrustedIssuer? trustedIssuer,
        IReadOnlyList<string> absent,
        int timeoutMs,
        CancellationToken budgetToken)
    {
        if (_callbackClient is null || trustedIssuer is null)
        {
            return (null, trustedIssuer is null ? "missing data is not fetched without an authenticated EHR caller" : null, null);
        }

        if (!instance.FetchMissingData)
        {
            return (null, "Fetch missing data is off for this service", null);
        }

        if (!trustedIssuer.AllowCallback)
        {
            return (null, "callbacks are off for this trusted issuer", null);
        }

        if (!trustedIssuer.IsTrustedFhirServer(request.FhirServer))
        {
            return (null, "fhirServer is not the trusted issuer's FHIR base URL, so it was not called", null);
        }

        var fallback = handler.GetFallbackQueries(instance.Hook);
        var queries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in absent)
        {
            if (fallback.TryGetValue(key, out var template) && CdsPrefetchTemplate.Resolve(template, request.Context) is { } query)
            {
                queries[key] = query;
            }
        }

        if (queries.Count == 0)
        {
            return (null, "no query to fetch the missing data with", null);
        }

        using var activity = FhirBridgeCdsMetrics.ActivitySource.StartActivity("cds.callback");
        activity?.SetTag("cds.callback.query_count", queries.Count);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var callbackTimeoutMs = Math.Clamp(
                await _settingsCache.GetIntAsync(CdsOptions.CallbackTimeoutMsKey, _options.CurrentValue.CallbackTimeoutMs, budgetToken),
                50,
                Math.Max(50, timeoutMs - 100));
            var result = await _callbackClient.FetchAsync(
                new CdsCallbackRequest(
                    request.FhirServer!,
                    trustedIssuer.EffectiveFhirBaseUrl,
                    request.FhirAuthorization,
                    queries,
                    TimeSpan.FromMilliseconds(callbackTimeoutMs)),
                budgetToken);
            activity?.SetTag("cds.callback.resource_count", result.ResourceCount);
            return (result, result.FailureReason is null ? null : $"callback: {result.FailureReason}", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a message could quote a URL holding a patient id.
            _logger.LogWarning("CDS callback for service {ServiceId} failed: {ErrorType}.", instance.ServiceId, exception.GetType().Name);
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            return (null, "callback failed", stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (budgetToken.IsCancellationRequested)
        {
            return (null, "callback ran out of time", stopwatch.ElapsedMilliseconds);
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length > maxLength ? value[..maxLength] + "…" : value;
}
