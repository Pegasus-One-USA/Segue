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
}
