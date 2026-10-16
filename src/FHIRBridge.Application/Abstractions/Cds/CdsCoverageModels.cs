using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Cds;

// Coverage Requirements Discovery (Da Vinci CRD STU2) — what the coverage engine (Infrastructure, does the I/O)
// hands the pure coverage-requirements handler. Holds request-derived data only in memory, for one call.

/// <summary>Where a coverage requirements answer came from.</summary>
public enum CdsCoverageSource
{
    /// <summary>No active Coverage in the request (prefetch "coverage").</summary>
    NoCoverage,

    /// <summary>Coverage found, but no enabled payer matches its payor / plan.</summary>
    NoPayer,

    /// <summary>Answered from Segue's own coverage rules for the matched payer.</summary>
    LocalRules,

    /// <summary>Relayed to the payer's own CRD service; <see cref="CdsCoveragePrepared.RelayResponse"/> holds its answer.</summary>
    Relay,

    /// <summary>The relay failed and the payer does not fall back to local rules: answer with nothing.</summary>
    RelayFailed,
}

/// <summary>One coverage decision (the CRD coverage-information values) for one order.</summary>
public sealed record CdsCoverageDecision(
    string Covered,
    string? PaNeeded,
    string? DocNeeded,
    IReadOnlyList<string> DocPurposes,
    IReadOnlyList<string> InfoNeeded,
    string? Reason,
    string? PolicyUrl,
    string? QuestionnaireCanonical,
    Guid? RuleId = null)
{
    public static CdsCoverageDecision FromRule(CdsCoverageRule rule) =>
        new(rule.Covered, rule.PaNeeded, rule.DocNeeded, rule.DocPurposes.ToArray(), rule.InfoNeeded.ToArray(), rule.Reason,
            rule.PolicyUrl, rule.QuestionnaireCanonical, rule.Id);

    /// <summary>"covered/pa-needed/doc-needed" with "-" for a value not stated (call log form).</summary>
    public string Codes => $"{Covered}/{PaNeeded ?? "-"}/{DocNeeded ?? "-"}";
}

/// <summary>The coverage engine's result for one call (<see cref="CdsHandlerContext.Prepared"/>).</summary>
/// <param name="CoverageReference">"Coverage/&lt;id&gt;" of the matched Coverage; null when it has no id.</param>
/// <param name="Decisions">Order key (see the handler's order list: "Type/id", or "#index" without an id) → decision; null = no rule matched.</param>
/// <param name="RelayResponse">The payer's CRD answer, as parsed (cards and systemActions are re-checked by the evaluator).</param>
public sealed record CdsCoveragePrepared(
    CdsCoverageSource Source,
    Guid? PayerId,
    string? PayerName,
    string? CoverageReference,
    IReadOnlyDictionary<string, CdsCoverageDecision?> Decisions,
    CdsResponse? RelayResponse = null,
    string? Note = null);

/// <summary>An in-memory copy of every enabled payer with its rules, for matching without a database read per call.</summary>
public sealed class CdsCoverageRuleSnapshot
{
    public CdsCoverageRuleSnapshot(IReadOnlyList<CdsPayer> payers, IReadOnlyList<CdsCoverageRule> rules)
    {
        Payers = payers.Where(payer => payer.IsEnabled).OrderBy(payer => payer.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var ids = Payers.Select(payer => payer.Id).ToHashSet();
        RulesByPayer = rules
            .Where(rule => rule.IsEnabled && ids.Contains(rule.PayerId))
            .GroupBy(rule => rule.PayerId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<CdsCoverageRule>)group.ToArray());
    }

    public static CdsCoverageRuleSnapshot Empty { get; } = new([], []);

    /// <summary>Enabled payers, by name.</summary>
    public IReadOnlyList<CdsPayer> Payers { get; }

    /// <summary>Enabled rules of enabled payers.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<CdsCoverageRule>> RulesByPayer { get; }

    public IReadOnlyList<CdsCoverageRule> RulesOf(Guid payerId) =>
        RulesByPayer.TryGetValue(payerId, out var rules) ? rules : [];
}

/// <summary>
/// The payer / rule snapshot the coverage engine matches against. Cached in memory; every admin change to payers or
/// rules calls <see cref="Invalidate"/> (other nodes pick changes up within a minute).
/// </summary>
public interface ICdsCoverageRuleCache
{
    Task<CdsCoverageRuleSnapshot> GetAsync(CancellationToken cancellationToken);

    void Invalidate();
}

/// <summary>
/// Sends a CDS Hooks request to a payer's own CRD service as Segue (intermediary): signed with Segue's key (RS384,
/// jku = Segue's JWKS URL), https only, guarded against private addresses, no redirects, bounded response.
/// Never throws for a remote failure.
/// </summary>
public interface ICdsPayerRelayClient
{
    Task<CdsRelayResult> SendAsync(CdsPayer payer, CdsRequest request, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <param name="Outcome">"ok" on success, else a short non-PHI reason ("timed out", "HTTP 503", "unreachable", "response too large", "invalid response", "not https", "Cds:PublicBaseUrl is not set").</param>
/// <param name="Response">The payer's parsed answer (only when <see cref="Succeeded"/>).</param>
public sealed record CdsRelayResult(bool Succeeded, string Outcome, int? HttpStatus, long DurationMs, CdsResponse? Response);

/// <summary>
/// Segue's public CDS JWK Set (GET /cds-services/.well-known/jwks.json): the key payers use to verify calls Segue
/// relays to their CRD services. Public key material only; the key is created on first use.
/// </summary>
public interface ICdsRelayJwksProvider
{
    Task<FHIRBridge.Application.DTOs.JsonWebKeySetDto> GetJwksAsync(CancellationToken cancellationToken);
}
