using FHIRBridge.Runtime.Domain.ValueObjects;

namespace FHIRBridge.Application.Abstractions.Aggregation;

/// <summary>
/// The outcome of a best-effort patient aggregation: every resource that was successfully retrieved, plus one
/// <see cref="ResourceFetchFailure"/> per resource type whose source query failed (after retries). A failed type
/// does NOT fail the whole request — the caller renders failures as <c>OperationOutcome</c> Bundle entries.
/// </summary>
public sealed record PatientAggregationResult(
    IReadOnlyList<ResourceEnvelope> Resources,
    IReadOnlyList<ResourceFetchFailure> Failures);
