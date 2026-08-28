using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using MySqlConnector;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Opens the MySQL connection + transaction a mapping-config import's DDL runs against — the MySQL counterpart
/// to <see cref="SqlServerMappingSchemaProvider"/>. Every mutating action within one
/// <see cref="IMappingSchemaTransaction"/> is preceded by an existence check so the import endpoint is safely
/// re-runnable, and the whole resourceType's DDL commits or rolls back as a unit.
/// </summary>
public sealed class MySqlMappingSchemaProvider : IMappingSchemaProvider
{
    private readonly ISecretProvider _secretProvider;

    public MySqlMappingSchemaProvider(ISecretProvider secretProvider)
    {
        _secretProvider = secretProvider;
    }

    public async Task<IMappingSchemaTransaction> BeginTransactionAsync(
        DestinationConfiguration destination, CancellationToken cancellationToken)
    {
        var connectionString = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        return new MySqlMappingSchemaTransaction(connection, transaction);
    }
}
