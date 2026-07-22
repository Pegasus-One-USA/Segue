using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Integration.Sql;
using Microsoft.Data.SqlClient;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Opens the SQL Server / Azure SQL connection + transaction a mapping-config import's DDL runs against. Every
/// mutating action within one <see cref="IMappingSchemaTransaction"/> is preceded by an existence check so the
/// import endpoint is safely re-runnable, and the whole resourceType's DDL commits or rolls back as a unit.
/// </summary>
public sealed class SqlServerMappingSchemaProvider : IMappingSchemaProvider
{
    private readonly ISecretProvider _secretProvider;

    public SqlServerMappingSchemaProvider(ISecretProvider secretProvider)
    {
        _secretProvider = secretProvider;
    }

    public async Task<IMappingSchemaTransaction> BeginTransactionAsync(
        DestinationConfiguration destination, CancellationToken cancellationToken)
    {
        var connectionString = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        var connection = await SqlServerConnectionFactory.OpenConnectionAsync(connectionString, cancellationToken);
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        return new SqlServerMappingSchemaTransaction(connection, transaction);
    }
}
