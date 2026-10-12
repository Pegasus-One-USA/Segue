using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfDeIdentificationProfileRepository : IDeIdentificationProfileRepository
{
    private readonly FHIRBridgeDbContext _db;

    public EfDeIdentificationProfileRepository(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<DeIdentificationProfile>> ListAsync(CancellationToken cancellationToken) =>
        await _db.DeIdentificationProfiles.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);

    public Task<DeIdentificationProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.DeIdentificationProfiles.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task AddAsync(DeIdentificationProfile profile, CancellationToken cancellationToken)
    {
        await _db.DeIdentificationProfiles.AddAsync(profile, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<DeIdentificationProfileUsage> FindUsageAsync(
        Guid profileId, Guid workflowId, CancellationToken cancellationToken)
    {
        var usedElsewhere = new List<string>();
        var ownDestinationIds = new List<Guid>();

        // A workflow references a policy or a connection by id inside its nodes' configuration JSON, so the id's
        // text is what to look for.
        var profileIdText = profileId.ToString();
        usedElsewhere.AddRange((await OtherWorkflowsReferencingAsync(profileIdText, workflowId, cancellationToken))
            .Select(name => $"workflow '{name}'"));

        var destinations = await _db.DestinationConfigurations.AsNoTracking()
            .Where(destination => destination.DeIdentificationProfileId == profileId)
            .Select(destination => new { destination.Id, destination.Name })
            .ToListAsync(cancellationToken);
        foreach (var destination in destinations)
        {
            var destinationIdText = destination.Id.ToString();
            var referencedHere = await _db.WorkflowNodes.AsNoTracking().AnyAsync(
                node => node.WorkflowDefinitionId == workflowId && node.ConfigurationJson.Contains(destinationIdText),
                cancellationToken);
            var otherWorkflows = await OtherWorkflowsReferencingAsync(destinationIdText, workflowId, cancellationToken);
            var v1Routes = await V1RoutesWritingToAsync(destination.Id, cancellationToken);

            if (referencedHere && otherWorkflows.Count == 0 && !v1Routes)
            {
                ownDestinationIds.Add(destination.Id);
            }
            else
            {
                usedElsewhere.Add($"destination connection '{destination.Name}'");
            }
        }

        return new DeIdentificationProfileUsage(usedElsewhere.Distinct().ToList(), ownDestinationIds);
    }

    public async Task ClearFromDestinationsAsync(
        Guid profileId, IReadOnlyCollection<Guid> destinationIds, CancellationToken cancellationToken)
    {
        var destinations = await _db.DestinationConfigurations
            .Where(destination => destinationIds.Contains(destination.Id) && destination.DeIdentificationProfileId == profileId)
            .ToListAsync(cancellationToken);
        foreach (var destination in destinations)
        {
            destination.SetDeIdentificationProfile(null);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private Task<List<string>> OtherWorkflowsReferencingAsync(string idText, Guid workflowId, CancellationToken cancellationToken) =>
        _db.WorkflowNodes.AsNoTracking()
            .Where(node => node.WorkflowDefinitionId != workflowId && node.ConfigurationJson.Contains(idText))
            .Join(_db.WorkflowDefinitions.AsNoTracking(), node => node.WorkflowDefinitionId, workflow => workflow.Id,
                (_, workflow) => workflow.Name)
            .Distinct()
            .ToListAsync(cancellationToken);

    /// <summary>Whether a V1 route writes to the destination — through its own mapping profile or one of its
    /// per-resource mappings. Such a route reads the de-identification profile from the connection itself.</summary>
    private Task<bool> V1RoutesWritingToAsync(Guid destinationId, CancellationToken cancellationToken)
    {
        var profilesForDestination = _db.MappingProfiles.AsNoTracking()
            .Where(profile => profile.DestinationId == destinationId)
            .Select(profile => profile.Id);
        return _db.ResourcePipelineRoutes.AsNoTracking().AnyAsync(
            route => profilesForDestination.Contains(route.MappingProfileId)
                || route.ResourceMappings.Any(mapping => profilesForDestination.Contains(mapping.MappingProfileId)),
            cancellationToken);
    }
}
