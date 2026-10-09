using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>An uploaded file without its content, for listing.</summary>
public sealed record TabularSourceFileSummary(
    Guid Id,
    string FileName,
    int SizeBytes,
    int RowCount,
    string ColumnsJson,
    string? CreatedBy,
    DateTime CreatedOnUtc);

/// <summary>Store for uploaded Tabular source CSVs (<see cref="TabularSourceFile"/>).</summary>
public interface ITabularSourceFileRepository
{
    Task AddAsync(TabularSourceFile file, CancellationToken cancellationToken);

    /// <summary>The file with its encrypted content.</summary>
    Task<TabularSourceFile?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Newest first, never loading the content.</summary>
    Task<IReadOnlyList<TabularSourceFileSummary>> ListAsync(int take, CancellationToken cancellationToken);

    /// <summary>Removes the file for good. False when it did not exist.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
