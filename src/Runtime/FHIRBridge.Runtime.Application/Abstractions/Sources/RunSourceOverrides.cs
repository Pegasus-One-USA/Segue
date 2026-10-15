namespace FHIRBridge.Runtime.Application.Abstractions.Sources;

/// <summary>Per-run replacements for the source connection's retrieval criteria, supplied by "Execute V2". A null
/// value keeps what is saved on the source connection; an empty string clears it for this run. Never persisted back
/// to the connection.
/// <para><paramref name="CriteriaByResourceType"/> (external trigger only), keyed by resource type, is combined for this
/// run with the saved per-resource-type criteria (the "Criteria" button) of each resource type it names: a parameter
/// both set takes this value, and the saved type's other parameters are kept. A type it does not name keeps its saved
/// criteria.</para></summary>
public sealed record RunSourceOverrides(
    string? GroupId,
    string? SearchCriteria,
    IReadOnlyDictionary<string, string>? CriteriaByResourceType = null);
