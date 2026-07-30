using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfUserFhirContextBindingRepository : IUserFhirContextBindingRepository
{
    private readonly FHIRBridgeDbContext _db;

    public EfUserFhirContextBindingRepository(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    public Task<UserFhirContextBinding?> GetAsync(Guid sourceConnectionId, string userIdentity, CancellationToken cancellationToken) =>
        _db.UserFhirContextBindings.FirstOrDefaultAsync(
            x => x.SourceConnectionId == sourceConnectionId && x.UserIdentity == userIdentity, cancellationToken);

    public async Task AddAsync(UserFhirContextBinding binding, CancellationToken cancellationToken)
    {
        await _db.UserFhirContextBindings.AddAsync(binding, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
