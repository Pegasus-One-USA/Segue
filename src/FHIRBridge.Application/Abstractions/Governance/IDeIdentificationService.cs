namespace FHIRBridge.Application.Abstractions.Governance;

public interface IDeIdentificationService
{
    Task<DeIdentificationResult> DeIdentifyAsync(
        DeIdentificationRequest request,
        CancellationToken cancellationToken);
}

public sealed record DeIdentificationRequest(
    string ResourceType,
    string? ResourceId,
    string RawJson,
    IReadOnlyCollection<string> AppliedPolicies,
    // Which DeIdentificationProfile's pre-mapping rules to apply. Null means "no profile assigned" — the
    // implementation should return RawJson unchanged rather than falling back to some other rule set, since
    // profiles (not a tenant-wide default) are now the unit of "what redaction applies here."
    Guid? ProfileId = null);

/// <summary>The de-identified JSON plus a per-field audit trail of what was actually changed — <see cref="Hops"/>
/// is empty whenever no profile was assigned or no rule matched anything in this resource. Consumed by
/// <c>DeIdentificationNodeExecutor</c> to thread PreMapping redactions into the same Field Lineage chain the
/// Mapping node builds for its own PostMapping rules (see <c>MappingNodeExecutor.ApplyTransformRulesAsync</c>).</summary>
public sealed record DeIdentificationResult(string Json, IReadOnlyList<DeIdentificationFieldHop> Hops);

/// <summary>One field-level redaction applied by a PreMapping de-identification rule — the PreMapping
/// counterpart to <c>LineageHopEntryDto</c> (which only ever covers PostMapping rule chains).</summary>
public sealed record DeIdentificationFieldHop(
    string SourceField,
    string Strategy,
    string ConfigJson,
    string? BeforeValueJson,
    string? AfterValueJson,
    bool Success,
    string? ErrorMessage);
