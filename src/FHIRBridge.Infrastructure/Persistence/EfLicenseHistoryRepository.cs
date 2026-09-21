using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities.Licensing;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfLicenseHistoryRepository : ILicenseHistoryRepository
{
    private readonly FHIRBridgeDbContext _db;

    public EfLicenseHistoryRepository(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(LicenseHistoryEntry entry, CancellationToken cancellationToken)
    {
        await _db.LicenseHistoryEntries.AddAsync(entry, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LicenseHistoryEntry>> GetAllAsync(CancellationToken cancellationToken) =>
        await _db.LicenseHistoryEntries
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.AppliedUtc)
            .ToListAsync(cancellationToken);

    // ExecuteUpdateAsync bypasses the change tracker (and AuditingSaveChangesInterceptor) — same pattern
    // EfUserAccessRepository uses for its own bulk operations — but sets IsDeleted rather than physically
    // removing the rows, so a "Clear License History" that turns out to have been a mistake is recoverable
    // directly from the database.
    public async Task ClearAllAsync(CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        await _db.LicenseHistoryEntries
            .Where(x => !x.IsDeleted)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.IsDeleted, true)
                    .SetProperty(x => x.DeletedOnUtc, nowUtc),
                cancellationToken);
    }
}
