namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>
/// One-time move of the retired source-level search criteria into per-resource-type criteria rows, so workflows
/// keep filtering exactly as they did before runs stopped reading the source's own Search criteria. Run at startup,
/// after migrations, under the database bootstrap lock; records completion so it never runs twice.
/// </summary>
public interface IResourceTypeCriteriaBackfill
{
    Task EnsureAsync(CancellationToken cancellationToken);
}
