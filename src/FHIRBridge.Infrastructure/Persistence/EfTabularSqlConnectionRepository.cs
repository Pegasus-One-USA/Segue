using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfTabularSqlConnectionRepository : ITabularSqlConnectionRepository
{
    private readonly FHIRBridgeDbContext _db;

    public EfTabularSqlConnectionRepository(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(TabularSqlConnection connection, CancellationToken cancellationToken)
    {
        await _db.TabularSqlConnections.AddAsync(connection, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<TabularSqlConnection?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _db.TabularSqlConnections.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<TabularSqlConnection?> FindByNameAsync(string name, CancellationToken cancellationToken)
    {
        var lowered = name.Trim().ToLower();
        return _db.TabularSqlConnections.AsNoTracking().FirstOrDefaultAsync(x => x.Name.ToLower() == lowered, cancellationToken);
    }

    public async Task<IReadOnlyList<TabularSqlConnection>> ListAsync(CancellationToken cancellationToken) =>
        await _db.TabularSqlConnections.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);

    public Task UpdateAsync(TabularSqlConnection connection, CancellationToken cancellationToken) =>
        _db.SaveChangesAsync(cancellationToken);

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        await _db.TabularSqlConnections.Where(x => x.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;
}
