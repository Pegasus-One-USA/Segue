using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Application.Abstractions.Tabular;

/// <summary>Rows read from a tabular source. Column names keep their header spelling; lookups ignore case.</summary>
/// <param name="Truncated">More rows existed than the cap allowed.</param>
public sealed record TabularRows(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows,
    bool Truncated);

/// <summary>A read-only SQL query over a connection string held as a secret.</summary>
/// <param name="Engine">"sqlserver", "postgresql" or "mysql".</param>
public sealed record TabularSqlQuery(string Engine, SecretReference ConnectionSecret, string Query);

/// <summary>
/// Reads the rows of a Tabular source: an uploaded CSV (stored encrypted) or a SQL query. Implemented in
/// Infrastructure, which holds the database drivers and the encryption key; the Runtime executor only sees rows.
/// Row values are PHI: they are never logged, and errors name a row number and column, never a value.
/// </summary>
public interface ITabularRowReader
{
    Task<TabularRows> ReadFileAsync(Guid fileId, int maxRows, CancellationToken cancellationToken);

    Task<TabularRows> ReadSqlAsync(TabularSqlQuery query, int maxRows, CancellationToken cancellationToken);
}
