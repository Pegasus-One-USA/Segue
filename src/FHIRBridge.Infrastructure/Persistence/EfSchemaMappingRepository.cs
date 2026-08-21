using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfSchemaMappingRepository : ISchemaMappingRepository
{
    private readonly FHIRBridgeDbContext _db;

    public EfSchemaMappingRepository(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<SchemaMapping>> GetApprovedAsync(
        string sourceSystem, string resourceType, string destinationTable, CancellationToken cancellationToken) =>
        await _db.SchemaMappings
            .Where(x =>
                x.SourceSystem == sourceSystem &&
                x.ResourceType == resourceType &&
                x.DestinationTable == destinationTable &&
                x.Status == SchemaMappingStatus.Approved)
            .ToListAsync(cancellationToken);

    public Task<SchemaMapping?> FindAsync(
        string sourceSystem, string resourceType, string destinationTable, string destinationField,
        CancellationToken cancellationToken) =>
        _db.SchemaMappings.FirstOrDefaultAsync(
            x =>
                x.SourceSystem == sourceSystem &&
                x.ResourceType == resourceType &&
                x.DestinationTable == destinationTable &&
                x.DestinationField == destinationField,
            cancellationToken);

    public async Task AddAsync(SchemaMapping mapping, CancellationToken cancellationToken)
    {
        await _db.SchemaMappings.AddAsync(mapping, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task UpdateAsync(SchemaMapping mapping, CancellationToken cancellationToken) =>
        _db.SaveChangesAsync(cancellationToken);
}
