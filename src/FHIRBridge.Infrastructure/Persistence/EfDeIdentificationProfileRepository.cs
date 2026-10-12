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

    public async Task<IReadOnlyList<string>> FindUsesOutsideWorkflowAsync(
        Guid profileId, Guid workflowId, CancellationToken cancellationToken)
    {
        var destinations = await _db.DestinationConfigurations.AsNoTracking()
            .Where(destination => destination.DeIdentificationProfileId == profileId)
            .Select(destination => destination.Name)
            .ToListAsync(cancellationToken);

        // A workflow references a policy by id inside its nodes' configuration JSON (the De-identification node's
        // profileId, a destination node's deIdentificationProfileId), so the id's text is what to look for.
        var profileIdText = profileId.ToString();
        var workflows = await _db.WorkflowNodes.AsNoTracking()
            .Where(node => node.WorkflowDefinitionId != workflowId && node.ConfigurationJson.Contains(profileIdText))
            .Join(_db.WorkflowDefinitions.AsNoTracking(), node => node.WorkflowDefinitionId, workflow => workflow.Id,
                (_, workflow) => workflow.Name)
            .Distinct()
            .ToListAsync(cancellationToken);

        return destinations.Select(name => $"destination connection '{name}'")
            .Concat(workflows.Select(name => $"workflow '{name}'"))
            .ToList();
    }
}
