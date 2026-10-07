namespace FHIRBridge.Application.Abstractions.Governance;

public interface IDeIdentificationService
{
    Task<DeIdentificationResult> DeIdentifyAsync(
        DeIdentificationRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies the rule behind one <see cref="DeIdentificationFieldHop"/> (its Strategy and ConfigJson) to a value
    /// that has already been mapped and transformed — used when a mapped column's own Transformations must run
    /// before its De-identification rule. Same strategy definitions as <see cref="DeIdentifyAsync"/>: a string is
    /// redacted, a list of strings is redacted item by item, and anything without a string form is left as is,
    /// except Remove (and Redact of a non-string), which yield null.
    /// </summary>
    object? DeIdentifyValue(object? value, DeIdentificationFieldHop hop);
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
