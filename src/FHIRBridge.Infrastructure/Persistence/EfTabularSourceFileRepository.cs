using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfTabularSourceFileRepository : ITabularSourceFileRepository
{
    private readonly FHIRBridgeDbContext _db;

    public EfTabularSourceFileRepository(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(TabularSourceFile file, CancellationToken cancellationToken)
    {
        await _db.TabularSourceFiles.AddAsync(file, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<TabularSourceFile?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _db.TabularSourceFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlyList<TabularSourceFileSummary>> ListAsync(int take, CancellationToken cancellationToken) =>
        await _db.TabularSourceFiles.AsNoTracking()
            .OrderByDescending(x => x.CreatedOnUtc)
            .Take(Math.Clamp(take, 1, 500))
            .Select(x => new TabularSourceFileSummary(x.Id, x.FileName, x.SizeBytes, x.RowCount, x.ColumnsJson, x.CreatedBy, x.CreatedOnUtc))
            .ToListAsync(cancellationToken);

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        await _db.TabularSourceFiles.Where(x => x.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;
}
