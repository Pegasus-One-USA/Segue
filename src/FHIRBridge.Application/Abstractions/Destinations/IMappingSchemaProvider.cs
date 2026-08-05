using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// Applies the destination schema changes (new tables/columns, with relationships) implied by an imported
/// mapping configuration. Unlike <see cref="IDestinationSchemaService"/> (ad-hoc connections, single bare-PK
/// tables, one column at a time — built for the mapping canvas's interactive schema authoring), this operates
/// against an already-saved <see cref="DestinationConfiguration"/> and creates fully-columned tables (with an
/// optional parent foreign key) for the mapping-config import endpoint.
/// </summary>
public interface IMappingSchemaProvider
{
    /// <summary>
    /// Opens one connection/transaction that every DDL action for a single resourceType's import runs against
    /// (see <see cref="IMappingSchemaTransaction"/>), so a mid-resourceType failure rolls back every schema
    /// change already made for that resourceType instead of leaving them stranded. Caller must
    /// <c>await using</c> the result and call <see cref="IMappingSchemaTransaction.CommitAsync"/> explicitly —
    /// disposing without committing rolls back.
    /// </summary>
    Task<IMappingSchemaTransaction> BeginTransactionAsync(DestinationConfiguration destination, CancellationToken cancellationToken);
}
