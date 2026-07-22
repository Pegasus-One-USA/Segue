using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// One connection/transaction scope for all the destination DDL a single resourceType's import needs.
/// Every method here runs against the same underlying connection/transaction, so a failure partway through
/// (e.g. table 4 of 6 fails to create) can be rolled back as a unit instead of leaving tables 1–3 stranded.
/// Never disposed with a pending commit — <see cref="IAsyncDisposable.DisposeAsync"/> rolls back whatever
/// hasn't been committed yet.
/// </summary>
public interface IMappingSchemaTransaction : IAsyncDisposable
{
    Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken);

    Task<bool> ColumnExistsAsync(string tableName, string columnName, CancellationToken cancellationToken);

    Task CreateTableAsync(TableDefinitionDto table, CancellationToken cancellationToken);

    Task AddColumnAsync(string tableName, ColumnToAddDto column, CancellationToken cancellationToken);

    /// <summary>Reads the real data type of a pre-existing column on a pre-existing table (null if not found)
    /// — used to resolve <c>MappingField.ValueType</c> when a column is neither newly created nor newly added.</summary>
    Task<string?> GetColumnDataTypeAsync(string tableName, string columnName, CancellationToken cancellationToken);

    /// <summary>Commits every DDL action executed on this transaction so far. Call this only once the
    /// caller's own persistence (e.g. the MappingProfile/Fields write) has also succeeded, so a failure there
    /// rolls the destination schema changes back too instead of leaving them applied with nothing referencing
    /// them yet.</summary>
    Task CommitAsync(CancellationToken cancellationToken);
}
