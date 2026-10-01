namespace FHIRBridge.Runtime.Application.Abstractions.Sources;

/// <summary>Per-run replacements for the source connection's retrieval criteria, supplied by "Execute V2". A null
/// value keeps what is saved on the source connection; an empty string clears it for this run. Never persisted back
/// to the connection.</summary>
public sealed record RunSourceOverrides(string? GroupId, string? SearchCriteria);
