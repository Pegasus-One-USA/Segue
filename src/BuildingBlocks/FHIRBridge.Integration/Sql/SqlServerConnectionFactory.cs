using Microsoft.Data.SqlClient;

namespace FHIRBridge.Integration.Sql;

/// <summary>
/// Opens SQL Server / Azure SQL connections for destination writers. The customer owns and manages the destination
/// database; a missing database surfaces as a normal SQL connection error rather than being auto-created.
/// </summary>
public static class SqlServerConnectionFactory
{
    public static async Task<SqlConnection> OpenConnectionAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        return connection;
    }
}
