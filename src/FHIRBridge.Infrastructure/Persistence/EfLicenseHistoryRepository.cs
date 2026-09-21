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
        await _db.LicenseHistoryEntries.OrderByDescending(x => x.AppliedUtc).ToListAsync(cancellationToken);

    // ExecuteDeleteAsync bypasses the change tracker (and AuditingSaveChangesInterceptor) for a genuine
    // hard delete — same pattern EfUserAccessRepository uses for its own bulk-clear operations.
    public async Task ClearAllAsync(CancellationToken cancellationToken) =>
        await _db.LicenseHistoryEntries.ExecuteDeleteAsync(cancellationToken);
}
