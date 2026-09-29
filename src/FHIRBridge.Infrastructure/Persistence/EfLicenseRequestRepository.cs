using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities.Licensing;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfLicenseRequestRepository : ILicenseRequestRepository
{
    private readonly FHIRBridgeDbContext _db;

    public EfLicenseRequestRepository(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<LicenseRequest>> ListAsync(CancellationToken cancellationToken) =>
        await _db.LicenseRequests
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.CreatedUtc)
            .ToListAsync(cancellationToken);

    public async Task<LicenseRequest?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await _db.LicenseRequests.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<LicenseRequest?> GetByUniqueKeyAsync(string uniqueKey, CancellationToken cancellationToken) =>
        await _db.LicenseRequests.FirstOrDefaultAsync(x => x.UniqueKey == uniqueKey, cancellationToken);

    public async Task AddAsync(LicenseRequest request, CancellationToken cancellationToken)
    {
        await _db.LicenseRequests.AddAsync(request, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(LicenseRequest request, CancellationToken cancellationToken) =>
        await _db.SaveChangesAsync(cancellationToken);
}
