namespace FHIRBridge.Runtime.Domain.ValueObjects;

/// <summary>
/// Records that a single patient-compartment resource type could not be fetched during best-effort aggregation.
/// Surfaced as an <c>OperationOutcome</c> entry in the merged searchset Bundle rather than failing the whole request.
/// </summary>
public sealed record ResourceFetchFailure(string ResourceType, string Message);
