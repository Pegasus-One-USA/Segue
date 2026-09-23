namespace FHIRBridge.Application.DTOs;

public sealed record TerminologyLookupResult(
    string System,
    string Code,
    string? Display,
    string? Version,
    string Source,
    /// <summary>The source's own abbreviated description, where it publishes one. Null for systems whose
    /// release carries only a single description string, and for lookups served by a remote terminology
    /// server rather than the local store.</summary>
    string? ShortDescription = null,
    /// <summary>The source's own full-length description.</summary>
    string? LongDescription = null,
    /// <summary>LOINC's LONG_COMMON_NAME and its per-source equivalents (SNOMED FSN, RxNorm prescribable
    /// name).</summary>
    string? LongCommonName = null,
    /// <summary>Whether the concept is currently in force, as computed at import from the source's own
    /// status or expiry signal. True for sources that publish no such signal.</summary>
    bool IsActive = true);
