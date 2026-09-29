using Npgsql;

namespace FHIRBridge.LicenseServer.Data;

/// <summary>
/// Ensures the target Postgres database named in a connection string actually exists before EF Core
/// migrations run against it. <c>Database.MigrateAsync()</c> can create tables inside an existing
/// database but — like most Postgres tooling — cannot create the database itself, since that requires a
/// separate connection to a database that's guaranteed to already exist (Postgres's own <c>postgres</c>
/// maintenance database). This lets the app be handed ANY connection string (a client's Azure Postgres, a
/// different local instance, a fresh CI container, anything) and be self-sufficient on startup — no
/// dependency on the local dev restart script, psql.exe, or any other out-of-process tooling having run
/// first.
/// </summary>
public static class PostgresDatabaseInitializer
{
    public static async Task EnsureDatabaseExistsAsync(
        string connectionString,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var targetBuilder = new NpgsqlConnectionStringBuilder(connectionString);
        var targetDatabase = targetBuilder.Database;
        if (string.IsNullOrWhiteSpace(targetDatabase))
        {
            throw new InvalidOperationException("The configured connection string does not specify a Database.");
        }

        // Postgres has no "CREATE DATABASE IF NOT EXISTS" - connect to the always-present "postgres"
        // maintenance database on the same server/credentials, check pg_database, then create if missing.
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };

        await using var connection = new NpgsqlConnection(maintenanceBuilder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name";
        checkCommand.Parameters.AddWithValue("name", targetDatabase);
        var exists = await checkCommand.ExecuteScalarAsync(cancellationToken) is not null;

        if (exists)
        {
            logger.LogInformation(
                "Postgres database {Database} already exists on {Host}:{Port}.",
                targetDatabase,
                targetBuilder.Host,
                targetBuilder.Port);
            return;
        }

        logger.LogInformation(
            "Postgres database {Database} does not exist on {Host}:{Port} - creating it.",
            targetDatabase,
            targetBuilder.Host,
            targetBuilder.Port);

        // Database names can't be parameterized in DDL; quote as an identifier instead (doubling any
        // embedded quote, the standard Postgres identifier-escaping rule).
        var quotedDatabase = targetDatabase.Replace("\"", "\"\"");
        await using var createCommand = connection.CreateCommand();
        createCommand.CommandText = $"CREATE DATABASE \"{quotedDatabase}\"";
        await createCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
