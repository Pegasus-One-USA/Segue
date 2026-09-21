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

    public async Task<LicenseRequest?> GetAsync(CancellationToken cancellationToken) =>
        await _db.LicenseRequests.OrderBy(x => x.CreatedUtc).FirstOrDefaultAsync(cancellationToken);

    public async Task AddAsync(LicenseRequest request, CancellationToken cancellationToken)
    {
        await _db.LicenseRequests.AddAsync(request, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(LicenseRequest request, CancellationToken cancellationToken) =>
        await _db.SaveChangesAsync(cancellationToken);
}
