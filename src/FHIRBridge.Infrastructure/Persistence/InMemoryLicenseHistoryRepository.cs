using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities.Licensing;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class InMemoryLicenseHistoryRepository : ILicenseHistoryRepository
{
    private readonly ConcurrentBag<LicenseHistoryEntry> _entries = new();

    public Task AddAsync(LicenseHistoryEntry entry, CancellationToken cancellationToken)
    {
        _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LicenseHistoryEntry>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LicenseHistoryEntry>>(
            _entries.OrderByDescending(x => x.AppliedUtc).ToArray());
}
