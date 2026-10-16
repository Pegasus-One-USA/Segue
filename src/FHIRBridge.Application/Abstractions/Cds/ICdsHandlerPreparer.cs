using FHIRBridge.Application.DTOs.Cds;

namespace FHIRBridge.Application.Abstractions.Cds;

/// <summary>
/// Work a handler TYPE needs done before its (pure, I/O-free) rules run — e.g. the coverage requirements handler
/// needs the payer's rules from the database, or an answer relayed from the payer's own CRD service. Registered in
/// DI and found by <see cref="HandlerKey"/> through a dictionary in the evaluator (never a switch); a handler with no
/// preparer runs straight away. The preparer runs inside the call's time budget, after missing prefetch was fetched.
/// <para>
/// Like a handler, a preparer must never log or keep anything from the request, and never throw for remote
/// failures: it returns a <see cref="CdsPreparation"/> with a short non-PHI note instead.
/// </para>
/// </summary>
public interface ICdsHandlerPreparer
{
    /// <summary>The <see cref="ICdsServiceHandler.Key"/> this preparer serves.</summary>
    string HandlerKey { get; }

    Task<CdsPreparation> PrepareAsync(CdsPreparationContext context, CancellationToken cancellationToken);
}

/// <summary>What a preparer gets.</summary>
/// <param name="Request">The CDS request (PHI: read, never log or keep), with callback results already merged into prefetch.</param>
/// <param name="Remaining">What is left of the call's time budget.</param>
/// <param name="IsLiveCall">
/// True for an authenticated EHR call; false for the admin Test screen, which must never send request data
/// anywhere (so, for example, no payer relay).
/// </param>
public sealed record CdsPreparationContext(
    CdsInstanceSnapshot Instance,
    CdsRequest Request,
    CdsSettingsValues Settings,
    TimeSpan Remaining,
    bool IsLiveCall);

/// <summary>What a preparer produced.</summary>
/// <param name="Data">Handed to the handler as <see cref="CdsHandlerContext.Prepared"/> (e.g. <see cref="CdsCoveragePrepared"/>).</param>
/// <param name="Coverage">Non-PHI coverage requirements facts for the call log; null for other handlers.</param>
/// <param name="Note">A short non-PHI note for the call log / Test screen (e.g. "relay timed out; answered from local rules").</param>
public sealed record CdsPreparation(object? Data, CdsCoverageCallInfo? Coverage = null, string? Note = null)
{
    public static CdsPreparation None { get; } = new((object?)null);
}

/// <summary>Coverage requirements facts recorded on the call log row (configuration and codes only, never patient data).</summary>
/// <param name="Decisions">"covered/pa-needed/doc-needed" per order, comma-separated ("-" for not stated).</param>
/// <param name="RelayUsed">True when the payer's CRD service was called.</param>
/// <param name="RelayOutcome">"ok", "timed out", "HTTP 503", "unreachable", "invalid response", ...; null when not relayed.</param>
public sealed record CdsCoverageCallInfo(
    Guid? PayerId,
    string? PayerName,
    string? Decisions,
    bool RelayUsed,
    long? RelayMs,
    string? RelayOutcome);
