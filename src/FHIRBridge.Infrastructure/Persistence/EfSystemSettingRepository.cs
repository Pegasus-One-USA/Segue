using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfSystemSettingRepository : ISystemSettingRepository
{
    private readonly FHIRBridgeDbContext _db;

    public EfSystemSettingRepository(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<SystemSetting>> GetAllAsync(CancellationToken cancellationToken) =>
        await _db.SystemSettings.OrderBy(x => x.Key).ToListAsync(cancellationToken);

    public async Task<SystemSetting?> GetByKeyAsync(string key, CancellationToken cancellationToken) =>
        await _db.SystemSettings.FirstOrDefaultAsync(x => x.Key == key, cancellationToken);

    public async Task<SystemSetting> UpsertAsync(
        string key, string value, string? description, CancellationToken cancellationToken)
    {
        var existing = await GetByKeyAsync(key, cancellationToken);
        if (existing is not null)
        {
            existing.UpdateValue(value, description);
        }
        else
        {
            existing = new SystemSetting(key, value, description);
            await _db.SystemSettings.AddAsync(existing, cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var existing = await GetByKeyAsync(key, cancellationToken);
        if (existing is null)
        {
            return;
        }

        _db.SystemSettings.Remove(existing);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
