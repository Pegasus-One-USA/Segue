using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using Npgsql;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Opens the PostgreSQL connection + transaction a mapping-config import's DDL runs against — the PostgreSQL
/// counterpart to <see cref="SqlServerMappingSchemaProvider"/>/<see cref="MySqlMappingSchemaProvider"/>. Every
/// mutating action within one <see cref="IMappingSchemaTransaction"/> is preceded by an existence check so the
/// import endpoint is safely re-runnable, and the whole resourceType's DDL commits or rolls back as a unit.
/// </summary>
public sealed class PostgreSqlMappingSchemaProvider : IMappingSchemaProvider
{
    private readonly ISecretProvider _secretProvider;

    public PostgreSqlMappingSchemaProvider(ISecretProvider secretProvider)
    {
        _secretProvider = secretProvider;
    }

    public async Task<IMappingSchemaTransaction> BeginTransactionAsync(
        DestinationConfiguration destination, CancellationToken cancellationToken)
    {
        var connectionString = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        return new PostgreSqlMappingSchemaTransaction(connection, transaction);
    }
}
