namespace FHIRBridge.Domain.ValueObjects;

/// <summary>
/// One resource type a source FHIR endpoint exposes, as reported by its CapabilityStatement
/// (<c>rest[].resource[]</c>). <see cref="Interactions"/> carries the FHIR interaction codes the endpoint
/// supports for the type (e.g. <c>read</c>, <c>search-type</c>, <c>create</c>).
/// </summary>
public sealed record CapabilityResource(
    string ResourceType,
    IReadOnlyList<string> Interactions);
