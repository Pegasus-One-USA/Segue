using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>Persistence for schema-matching decisions (<see cref="SchemaMapping"/>) — the "learning" surface
/// that lets a previously approved field match short-circuit the scoring algorithm on future requests.</summary>
public interface ISchemaMappingRepository
{
    /// <summary>Rows with <see cref="Domain.Enums.SchemaMappingStatus.Approved"/> for this
    /// (source system, resource type, destination table) triple — these override scoring entirely.</summary>
    Task<IReadOnlyList<SchemaMapping>> GetApprovedAsync(
        string sourceSystem, string resourceType, string destinationTable, CancellationToken cancellationToken);

    /// <summary>Looks up any existing row (any status) for one destination field, so
    /// <c>SaveApprovedMappingAsync</c> can update in place rather than accumulate duplicates.</summary>
    Task<SchemaMapping?> FindAsync(
        string sourceSystem, string resourceType, string destinationTable, string destinationField,
        CancellationToken cancellationToken);

    Task AddAsync(SchemaMapping mapping, CancellationToken cancellationToken);

    Task UpdateAsync(SchemaMapping mapping, CancellationToken cancellationToken);
}
