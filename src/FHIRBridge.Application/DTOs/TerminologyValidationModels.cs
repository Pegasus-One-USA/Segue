namespace FHIRBridge.Application.DTOs;

/// <summary>Result of validating a code against a ValueSet binding (FHIR ValueSet/$validate-code).</summary>
public sealed record TerminologyValidationResult(
    bool IsValid,
    string? Message,
    string Source);

/// <summary>A single concept produced by expanding a ValueSet (FHIR ValueSet/$expand).</summary>
public sealed record TerminologyConcept(
    string System,
    string Code,
    string? Display);
