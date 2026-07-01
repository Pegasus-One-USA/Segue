namespace FHIRBridge.Application.DTOs;

public sealed record TerminologyLookupResult(
    string System,
    string Code,
    string? Display,
    string? Version,
    string Source);
