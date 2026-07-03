namespace FHIRBridge.Application.Abstractions.Normalization;

public interface IResourceNormalizationService
{
    Task<ResourceNormalizationResult> NormalizeAsync(
        ResourceNormalizationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// A single, composable stage of resource normalization (e.g. extension flattening, US Core validation,
/// data-quality scoring, patient matching). Steps run in order; each receives the running result and returns
/// an updated one so the JSON transform and accumulated diagnostics flow forward.
/// </summary>
public interface IResourceNormalizationStep
{
    /// <summary>Lower runs first. Flattening (10) precedes validation (20), scoring (30), matching (40).</summary>
    int Order { get; }

    Task<ResourceNormalizationResult> ApplyAsync(
        ResourceNormalizationRequest request,
        ResourceNormalizationResult current,
        CancellationToken cancellationToken);
}

public sealed record ResourceNormalizationRequest(
    Guid PipelineRunId,
    string ResourceType,
    string? ResourceId,
    string RawJson);

public sealed record ResourceNormalizationResult(
    string NormalizedJson,
    IReadOnlyCollection<string> AppliedProfiles,
    IReadOnlyCollection<string> Warnings,
    double? DataQualityScore = null,
    string? MasterPatientId = null);
