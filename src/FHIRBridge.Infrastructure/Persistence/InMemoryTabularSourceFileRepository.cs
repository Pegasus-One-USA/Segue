using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>Uploads for the no-database host profile (integration tests, local runs without a database).</summary>
public sealed class InMemoryTabularSourceFileRepository : ITabularSourceFileRepository
{
    private readonly ConcurrentDictionary<Guid, TabularSourceFile> _files = new();

    public Task AddAsync(TabularSourceFile file, CancellationToken cancellationToken)
    {
        _files[file.Id] = file;
        return Task.CompletedTask;
    }

    public Task<TabularSourceFile?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_files.GetValueOrDefault(id));

    public Task<IReadOnlyList<TabularSourceFileSummary>> ListAsync(int take, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TabularSourceFileSummary>>(_files.Values
            .OrderByDescending(x => x.CreatedOnUtc)
            .Take(Math.Clamp(take, 1, 500))
            .Select(x => new TabularSourceFileSummary(x.Id, x.FileName, x.SizeBytes, x.RowCount, x.ColumnsJson, x.CreatedBy, x.CreatedOnUtc))
            .ToList());

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_files.TryRemove(id, out _));
}
