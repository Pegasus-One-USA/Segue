using FHIRBridge.Application.Abstractions.Normalization;

namespace FHIRBridge.Infrastructure.Normalization;

/// <summary>
/// Runs the registered <see cref="IResourceNormalizationStep"/> stages in <see cref="IResourceNormalizationStep.Order"/>
/// order, threading the JSON transform and accumulated diagnostics through each. A failing step is logged as a warning
/// and skipped so a single bad stage never fails the whole pipeline.
/// </summary>
public sealed class CompositeResourceNormalizationService : IResourceNormalizationService
{
    private readonly IReadOnlyList<IResourceNormalizationStep> _steps;

    public CompositeResourceNormalizationService(IEnumerable<IResourceNormalizationStep> steps)
    {
        _steps = steps.OrderBy(step => step.Order).ToList();
    }

    public async Task<ResourceNormalizationResult> NormalizeAsync(
        ResourceNormalizationRequest request,
        CancellationToken cancellationToken)
    {
        var current = new ResourceNormalizationResult(request.RawJson, [], []);

        foreach (var step in _steps)
        {
            current = await step.ApplyAsync(request, current, cancellationToken);
        }

        return current;
    }
}
