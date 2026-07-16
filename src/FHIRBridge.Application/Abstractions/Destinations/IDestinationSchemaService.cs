using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Destinations;

public interface IDestinationSchemaService
{
    Task<DestinationSchemaDto> GetSchemaAsync(Guid destinationId, CancellationToken cancellationToken);

    /// <summary>
    /// Tests an ad-hoc (unsaved) relational connection and, on success, returns its tables/columns — powers the
    /// builder's "test connection → pick table/column" flow. Never throws for connection failures; returns
    /// <c>Connected=false</c> + <c>Error</c> instead.
    /// </summary>
    Task<DestinationSchemaProbeDto> ProbeSchemaAsync(
        DestinationConnectionProbeRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executes a real ALTER TABLE against an ad-hoc SQL Server / Azure SQL connection. Never throws for
    /// connection/SQL failures; returns <c>Success=false</c> + <c>Error</c> instead.
    /// </summary>
    Task<SchemaMutationResultDto> AddColumnAsync(AddColumnRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Executes a real CREATE TABLE (single auto-increment Id primary key) against an ad-hoc SQL Server /
    /// Azure SQL connection. Fails (does not throw) if the table already exists.
    /// </summary>
    Task<SchemaMutationResultDto> CreateTableAsync(CreateTableRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Executes a real, irreversible ALTER TABLE ... DROP COLUMN against an ad-hoc SQL Server / Azure SQL
    /// connection. Never throws for connection/SQL failures; returns <c>Success=false</c> + <c>Error</c>
    /// instead. Confirming this with the user is the caller's responsibility.
    /// </summary>
    Task<SchemaMutationResultDto> DropColumnAsync(DropColumnRequest request, CancellationToken cancellationToken);
}
