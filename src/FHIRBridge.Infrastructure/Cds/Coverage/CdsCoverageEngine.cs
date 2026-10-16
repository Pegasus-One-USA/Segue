using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Cds.Handlers;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Cds.Coverage;

/// <summary>
/// The I/O half of coverage requirements (Da Vinci CRD), run by the evaluator before the pure
/// <see cref="CoverageRequirementsCdsHandler"/>: reads the active Coverage from prefetch (already completed by the
/// FHIR callback when missing), matches it to an enabled <see cref="CdsPayer"/>, then either relays the call to
/// the payer's own CRD service (live calls only) or applies the payer's rules from the in-memory snapshot. A failed
/// relay falls back to local rules when the payer allows it. Never logs or keeps request data; notes are non-PHI.
/// </summary>
public sealed class CdsCoverageEngine : ICdsHandlerPreparer
{
    /// <summary>Time kept back from the relay so the rules and the response still fit the call's budget.</summary>
    private static readonly TimeSpan BudgetReserve = TimeSpan.FromMilliseconds(150);

    private static readonly TimeSpan MinimumRelayTimeout = TimeSpan.FromMilliseconds(100);

    private readonly ICdsCoverageRuleCache _cache;
    private readonly ICdsPayerRelayClient _relay;
    private readonly ILogger<CdsCoverageEngine> _logger;

    public CdsCoverageEngine(ICdsCoverageRuleCache cache, ICdsPayerRelayClient relay, ILogger<CdsCoverageEngine> logger)
    {
        _cache = cache;
        _relay = relay;
        _logger = logger;
    }

    public string HandlerKey => CdsHandlerKeys.CoverageRequirements;

    public async Task<CdsPreparation> PrepareAsync(CdsPreparationContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var coverages = CdsCoverageMatcher.ActiveCoverages(request);
        if (coverages.Count == 0)
        {
            const string none = "No active Coverage in prefetch 'coverage'; no coverage information.";
            return new CdsPreparation(Empty(CdsCoverageSource.NoCoverage, none), new CdsCoverageCallInfo(null, null, null, false, null, null), none);
        }

        var snapshot = await _cache.GetAsync(cancellationToken);
        var match = CdsCoverageMatcher.MatchPayer(coverages, request.Prefetch?[CdsCoverageMatcher.CoveragePrefetchKey], snapshot.Payers);
        if (match is null)
        {
            const string noPayer = "No enabled payer matches the patient's coverage.";
            return new CdsPreparation(Empty(CdsCoverageSource.NoPayer, noPayer), new CdsCoverageCallInfo(null, null, null, false, null, null), noPayer);
        }

        var payer = match.Payer;
        if (!payer.UsesRelay)
        {
            return Local(snapshot, request, match, null);
        }

        if (!context.IsLiveCall)
        {
            // The Test screen never sends pasted data anywhere: the payer's relay has its own Test relay button.
            return Local(snapshot, request, match, new RelayFacts(false, null, null,
                "Test run: not relayed to the payer's CRD service; answered from local rules."));
        }

        var timeout = TimeSpan.FromMilliseconds(payer.RelayTimeoutMs);
        var available = context.Remaining - BudgetReserve;
        if (available < timeout)
        {
            timeout = available;
        }

        CdsRelayResult result;
        if (timeout < MinimumRelayTimeout)
        {
            result = new CdsRelayResult(false, "no time left in the call's budget", null, 0, null);
        }
        else
        {
            try
            {
                result = await _relay.SendAsync(payer, RelayRequest(request, payer), timeout, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The type only: a message could quote the payer URL or request content.
                _logger.LogWarning("CDS coverage relay for payer {PayerId} failed: {ErrorType}.", payer.Id, exception.GetType().Name);
                result = new CdsRelayResult(false, "relay failed", null, 0, null);
            }
        }

        if (result.Succeeded && result.Response is { } response)
        {
            var decisions = string.Join(",", (response.SystemActions ?? [])
                .SelectMany(action => CdsCoverageInformation.Read(action?.Resource))
                .Select(entry => entry.Decision.Codes));
            var prepared = new CdsCoveragePrepared(
                CdsCoverageSource.Relay, payer.Id, payer.Name, match.CoverageReference,
                new Dictionary<string, CdsCoverageDecision?>(), response);
            return new CdsPreparation(
                prepared,
                new CdsCoverageCallInfo(payer.Id, payer.Name, decisions.Length == 0 ? null : decisions, true, result.DurationMs, "ok"),
                null);
        }

        if (payer.FallbackToLocalRules)
        {
            return Local(snapshot, request, match, new RelayFacts(true, result.DurationMs, $"{result.Outcome}; local rules used",
                $"The payer's CRD service did not answer ({result.Outcome}); answered from local rules."));
        }

        var failed = $"The payer's CRD service did not answer ({result.Outcome}); answered with no cards.";
        return new CdsPreparation(
            new CdsCoveragePrepared(CdsCoverageSource.RelayFailed, payer.Id, payer.Name, match.CoverageReference,
                new Dictionary<string, CdsCoverageDecision?>(), null, failed),
            new CdsCoverageCallInfo(payer.Id, payer.Name, null, true, result.DurationMs, result.Outcome),
            failed);
    }

    /// <summary>
    /// The request Segue sends the payer: the same hook, context and prefetch (including what the callback fetched),
    /// a new hookInstance, no vendor extensions, and fhirServer / fhirAuthorization only when the payer is allowed
    /// to receive the EHR's token.
    /// </summary>
    public static CdsRequest RelayRequest(CdsRequest inbound, CdsPayer payer) => new()
    {
        Hook = inbound.Hook,
        HookInstance = Guid.NewGuid().ToString(),
        Context = inbound.Context?.DeepClone() as JsonObject,
        Prefetch = inbound.Prefetch?.DeepClone() as JsonObject,
        FhirServer = payer.ForwardFhirAuthorization ? inbound.FhirServer : null,
        FhirAuthorization = payer.ForwardFhirAuthorization ? inbound.FhirAuthorization : null,
    };

    private static CdsPreparation Local(CdsCoverageRuleSnapshot snapshot, CdsRequest request, CdsCoverageMatch match, RelayFacts? relay)
    {
        var rules = snapshot.RulesOf(match.Payer.Id);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var decisions = new Dictionary<string, CdsCoverageDecision?>(StringComparer.Ordinal);
        foreach (var item in CdsCrdItems.Extract(request))
        {
            var rule = CdsCoverageMatcher.MatchRule(item.Codes, rules, today);
            decisions[item.Key] = rule is null ? null : CdsCoverageDecision.FromRule(rule);
        }

        var codes = string.Join(",", decisions.Values.OfType<CdsCoverageDecision>().Select(decision => decision.Codes));
        var prepared = new CdsCoveragePrepared(
            CdsCoverageSource.LocalRules, match.Payer.Id, match.Payer.Name, match.CoverageReference, decisions, null, relay?.Note);
        return new CdsPreparation(
            prepared,
            new CdsCoverageCallInfo(match.Payer.Id, match.Payer.Name, codes.Length == 0 ? null : codes, relay?.Used ?? false, relay?.Ms, relay?.Outcome),
            relay?.Note);
    }

    private static CdsCoveragePrepared Empty(CdsCoverageSource source, string note) =>
        new(source, null, null, null, new Dictionary<string, CdsCoverageDecision?>(), null, note);

    private sealed record RelayFacts(bool Used, long? Ms, string? Outcome, string Note);
}
