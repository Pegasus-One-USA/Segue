using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>Saved databases for the no-database host profile (integration tests, local runs without a database).</summary>
public sealed class InMemoryTabularSqlConnectionRepository : ITabularSqlConnectionRepository
{
    private readonly ConcurrentDictionary<Guid, TabularSqlConnection> _connections = new();

    public Task AddAsync(TabularSqlConnection connection, CancellationToken cancellationToken)
    {
        _connections[connection.Id] = connection;
        return Task.CompletedTask;
    }

    public Task<TabularSqlConnection?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_connections.GetValueOrDefault(id));

    public Task<TabularSqlConnection?> FindByNameAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(_connections.Values.FirstOrDefault(x => string.Equals(x.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<TabularSqlConnection>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TabularSqlConnection>>(_connections.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList());

    public Task UpdateAsync(TabularSqlConnection connection, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_connections.TryRemove(id, out _));
}
