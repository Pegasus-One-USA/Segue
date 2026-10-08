using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>Saved databases for CSV / SQL Table sources. Holds references to secrets, never connection strings.</summary>
public interface ITabularSqlConnectionRepository
{
    Task AddAsync(TabularSqlConnection connection, CancellationToken cancellationToken);

    Task<TabularSqlConnection?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>By name, ignoring case.</summary>
    Task<TabularSqlConnection?> FindByNameAsync(string name, CancellationToken cancellationToken);

    /// <summary>By name, A to Z.</summary>
    Task<IReadOnlyList<TabularSqlConnection>> ListAsync(CancellationToken cancellationToken);

    Task UpdateAsync(TabularSqlConnection connection, CancellationToken cancellationToken);

    /// <summary>False when it did not exist.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
