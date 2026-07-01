using Microsoft.Data.SqlClient;

namespace FHIRBridge.Integration.Sql;

/// <summary>
/// Opens SQL Server / Azure SQL connections for destination writers, creating the target database first when it does
/// not yet exist. Shared by every SQL-based destination writer so the bootstrap logic lives in one place.
/// </summary>
public static class SqlServerConnectionFactory
{
    public static async Task<SqlConnection> OpenConnectionAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var databaseName = builder.InitialCatalog;

        if (!string.IsNullOrWhiteSpace(databaseName) &&
            !databaseName.Equals("master", StringComparison.OrdinalIgnoreCase))
        {
            var masterBuilder = new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = "master"
            };

            await using var masterConnection = new SqlConnection(masterBuilder.ConnectionString);
            await masterConnection.OpenAsync(cancellationToken);

            await using var createDatabaseCommand = new SqlCommand(
                $"""
                IF DB_ID(@DatabaseName) IS NULL
                BEGIN
                    EXEC(N'CREATE DATABASE [{SqlIdentifier.Escape(databaseName)}]')
                END
                """,
                masterConnection);

            createDatabaseCommand.Parameters.AddWithValue("@DatabaseName", databaseName);
            await createDatabaseCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        return connection;
    }
}
