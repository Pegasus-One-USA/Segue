using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfTransformationRuleRepository : ITransformationRuleRepository
{
    private readonly FHIRBridgeDbContext _db;

    public EfTransformationRuleRepository(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    // All five scope tiers below back only IEffectiveRuleResolver's PostMapping resolution (the per-destination-
    // field pass that runs automatically after every mapped row — see TransformNodeExecutors.ApplyTransformRulesAsync).
    // PreMapping rules (e.g. the HIPAA Safe Harbor default profile, applied by SafeHarborDeIdentificationService
    // against raw FHIR JSON via SourceField paths, only when a Compliance node runs) must never be returned here:
    // they carry no DestinationField (they target a SourceField instead), so the "DestinationField == null is a
    // wildcard" fallback below would otherwise match EVERY destination field for that resource type/scope,
    // masking fields the rule was never meant to touch.
    public async Task<IReadOnlyList<TransformationRule>> GetWorkflowScopedAsync(
        Guid resourcePipelineRouteId, string resourceType, string destinationField, string? sourceSystem,
        string? sourceField, CancellationToken cancellationToken) =>
        await _db.TransformationRules
            .Where(x =>
                x.ExecutionPhase == TransformExecutionPhase.PostMapping &&
                x.Scope == TransformScope.Workflow &&
                x.ResourcePipelineRouteId == resourcePipelineRouteId &&
                x.ResourceType == resourceType &&
                x.DestinationField == destinationField &&
                (x.SourceSystem == null || x.SourceSystem == sourceSystem) &&
                (x.SourceField == null || x.SourceField == sourceField))
            .ToListAsync(cancellationToken);

    // ResourceType and DestinationField are null-or-match here (not required exact, unlike Workflow scope) —
    // a Field rule can be authored "by source field alone," matching wherever that source field is mapped
    // regardless of resource type or destination column name, same "specific beats blanket" pattern the
    // broader tiers already use for DestinationField.
    public async Task<IReadOnlyList<TransformationRule>> GetFieldScopedAsync(
        string resourceType, string destinationField, string? sourceSystem, string? sourceField,
        CancellationToken cancellationToken) =>
        await _db.TransformationRules
            .Where(x =>
                x.ExecutionPhase == TransformExecutionPhase.PostMapping &&
                x.Scope == TransformScope.Field &&
                (x.ResourceType == null || x.ResourceType == resourceType) &&
                (x.DestinationField == null || x.DestinationField == destinationField) &&
                (x.SourceSystem == null || x.SourceSystem == sourceSystem) &&
                (x.SourceField == null || x.SourceField == sourceField))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TransformationRule>> GetResourceTypeScopedAsync(
        string resourceType, string? destinationField, CancellationToken cancellationToken) =>
        await _db.TransformationRules
            .Where(x =>
                x.ExecutionPhase == TransformExecutionPhase.PostMapping &&
                x.Scope == TransformScope.ResourceType &&
                x.ResourceType == resourceType &&
                (x.DestinationField == null || x.DestinationField == destinationField))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TransformationRule>> GetDestinationTypeScopedAsync(
        DestinationType destinationType, string? destinationField, CancellationToken cancellationToken) =>
        await _db.TransformationRules
            .Where(x =>
                x.ExecutionPhase == TransformExecutionPhase.PostMapping &&
                x.Scope == TransformScope.DestinationType &&
                x.DestinationType == destinationType &&
                (x.DestinationField == null || x.DestinationField == destinationField))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TransformationRule>> GetGlobalScopedAsync(
        string? destinationField, CancellationToken cancellationToken) =>
        await _db.TransformationRules
            .Where(x =>
                x.ExecutionPhase == TransformExecutionPhase.PostMapping &&
                x.Scope == TransformScope.Global &&
                (x.DestinationField == null || x.DestinationField == destinationField))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TransformationRule>> GetPreMappingRulesAsync(
        Guid deIdentificationProfileId, string resourceType, CancellationToken cancellationToken) =>
        await _db.TransformationRules
            .Where(x =>
                x.ExecutionPhase == TransformExecutionPhase.PreMapping &&
                x.DeIdentificationProfileId == deIdentificationProfileId &&
                (x.Scope == TransformScope.Global ||
                 (x.Scope == TransformScope.ResourceType && x.ResourceType == resourceType)))
            .OrderBy(x => x.Order)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TransformationRule>> ListAsync(
        TransformScope? scope, DestinationType? destinationType, string? resourceType, string? destinationField,
        Guid? resourcePipelineRouteId, string? sourceSystem, string? sourceField, CancellationToken cancellationToken) =>
        await _db.TransformationRules
            .Where(x => scope == null || x.Scope == scope)
            .Where(x => destinationType == null || x.DestinationType == destinationType)
            .Where(x => resourceType == null || x.ResourceType == resourceType)
            .Where(x => destinationField == null || x.DestinationField == destinationField)
            .Where(x => resourcePipelineRouteId == null || x.ResourcePipelineRouteId == resourcePipelineRouteId)
            .Where(x => sourceSystem == null || x.SourceSystem == sourceSystem)
            .Where(x => sourceField == null || x.SourceField == sourceField)
            .OrderBy(x => x.Scope).ThenBy(x => x.Order)
            .ToListAsync(cancellationToken);

    public Task<TransformationRule?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.TransformationRules.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task AddAsync(TransformationRule rule, CancellationToken cancellationToken)
    {
        await _db.TransformationRules.AddAsync(rule, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task UpdateAsync(TransformationRule rule, CancellationToken cancellationToken) =>
        _db.SaveChangesAsync(cancellationToken);

    public Task DeleteAsync(TransformationRule rule, CancellationToken cancellationToken)
    {
        _db.TransformationRules.Remove(rule);
        return _db.SaveChangesAsync(cancellationToken);
    }
}
