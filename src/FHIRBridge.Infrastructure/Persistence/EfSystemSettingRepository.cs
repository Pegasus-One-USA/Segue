using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
            await _db.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var created = new SystemSetting(key, value, description);
        await _db.SystemSettings.AddAsync(created, cancellationToken);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return created;
        }
        catch (DbUpdateException exception) when (IsSystemSettingsKeyViolation(exception))
        {
            // Startup seeding (SystemSettingsSeeder) runs once per API/Worker instance with no distributed
            // lock, so two replicas can both see the key missing and race to insert it — the loser must not
            // crash the whole boot. The winner's row is what we want anyway, so just return it.
            //
            // IsSystemSettingsKeyViolation checks the constraint name (not just "was this a unique-key
            // violation"), because this DbContext is shared across every startup seeder in the same
            // BootstrapDatabase call — a stale entity left tracked by an unrelated failed seeder would
            // otherwise also surface here as a unique-key violation on SOME OTHER table, and GetByKeyAsync
            // below would correctly find nothing for `key` (it was never this row's insert that failed), so
            // returning null and pretending success would silently drop this setting instead of failing loudly.
            _db.Entry(created).State = EntityState.Detached;
            return await GetByKeyAsync(key, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"SystemSetting '{key}' insert failed on a unique-key violation but no row was found "
                    + "afterward — this looks like a stale, unrelated entity left tracked by another failed "
                    + "seeder sharing this DbContext, not a genuine concurrent insert of this same key.",
                    exception);
        }
    }

    private static bool IsSystemSettingsKeyViolation(DbUpdateException exception) =>
        exception.InnerException switch
        {
            PostgresException postgres =>
                postgres.SqlState == PostgresErrorCodes.UniqueViolation
                && postgres.ConstraintName == "IX_SystemSettings_Key",
            SqlException sql =>
                sql.Number is 2601 or 2627
                && sql.Message.Contains("IX_SystemSettings_Key", StringComparison.Ordinal),
            _ => false,
        };

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
