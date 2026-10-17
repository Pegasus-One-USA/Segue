using FHIRBridge.Infrastructure.Tabular;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace FHIRBridge.UnitTests.Tabular;

/// <summary>Runs only when TABULAR_IT_SQLSERVER_ADMIN is set; skipped otherwise.</summary>
public sealed class SqlServerAdminFactAttribute : TheoryAttribute
{
    public const string Variable = "TABULAR_IT_SQLSERVER_ADMIN";

    public SqlServerAdminFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Set {Variable} to a login on a THROWAWAY SQL Server that may create and drop databases.";
        }
    }
}

/// <summary>
/// A scratch database with one schema holding a table, an inline function, a procedure and a queue. Created when the
/// class's tests run and dropped afterwards; nothing happens when the variable is not set.
/// </summary>
public sealed class SqlServerScratchDatabase : IAsyncLifetime
{
    public string? ConnectionString { get; private set; }

    private string? _adminConnectionString;
    private string? _databaseName;

    public async Task InitializeAsync()
    {
        _adminConnectionString = Environment.GetEnvironmentVariable(SqlServerAdminFactAttribute.Variable);
        if (string.IsNullOrEmpty(_adminConnectionString))
        {
            return;
        }

        _databaseName = $"tabular_ro_check_{Guid.NewGuid():N}";
        await ExecuteAsync(Master(), $"CREATE DATABASE [{_databaseName}]");
        ConnectionString = new SqlConnectionStringBuilder(_adminConnectionString)
        {
            InitialCatalog = _databaseName,
            // EXECUTE AS stays on the session: never hand it back to the pool.
            Pooling = false,
        }.ConnectionString;

        // CREATE SCHEMA / PROCEDURE / FUNCTION must each be alone in their batch.
        await ExecuteAsync(ConnectionString, "CREATE SCHEMA app");
        await ExecuteAsync(ConnectionString, "CREATE TABLE app.t (id int, c int)");
        await ExecuteAsync(ConnectionString, "CREATE PROCEDURE app.p AS SELECT 1");
        await ExecuteAsync(ConnectionString, "CREATE FUNCTION app.f() RETURNS TABLE AS RETURN SELECT id FROM app.t");
        await ExecuteAsync(ConnectionString, "CREATE QUEUE app.q");
    }

    public async Task DisposeAsync()
    {
        if (_databaseName is null)
        {
            return;
        }

        SqlConnection.ClearAllPools();
        await ExecuteAsync(Master(), $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}]");
    }

    public static async Task<object?> ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private string Master() => new SqlConnectionStringBuilder(_adminConnectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString;
}

/// <summary>
/// Item 19 against a real SQL Server: the check that refuses a Tabular login able to change data, run as users
/// WITHOUT LOGIN that read through db_datareader and hold one extra right each. A plain reader, or one with an extra
/// right that cannot change anything, passes (0); every right that can change data is refused (1).
/// </summary>
public sealed class TabularSqlServerReadOnlyCheckTests : IClassFixture<SqlServerScratchDatabase>
{
    private readonly SqlServerScratchDatabase _database;

    public TabularSqlServerReadOnlyCheckTests(SqlServerScratchDatabase database)
    {
        _database = database;
    }

    public static TheoryData<string, string, string, int> Cases() => new()
    {
        // {u} is the case's user. Teardown runs even when the check fails; only rights given to every user need one.
        { "plain_reader", "", "", 0 },
        { "select_grant", "GRANT SELECT ON app.t TO [{u}]", "", 0 },
        { "view_definition", "GRANT VIEW DEFINITION ON SCHEMA::app TO [{u}]", "", 0 },
        { "schema_update", "GRANT UPDATE ON SCHEMA::app TO [{u}]", "", 1 },
        { "schema_insert", "GRANT INSERT ON SCHEMA::app TO [{u}]", "", 1 },
        { "table_delete_role", "CREATE ROLE [{u}_r]; GRANT DELETE ON app.t TO [{u}_r]; ALTER ROLE [{u}_r] ADD MEMBER [{u}]", "", 1 },
        { "table_update_with_grant_option", "GRANT UPDATE ON app.t TO [{u}] WITH GRANT OPTION", "", 1 },
        { "column_update", "GRANT UPDATE (c) ON app.t TO [{u}]", "", 1 },
        { "function_insert_role", "CREATE ROLE [{u}_r]; GRANT INSERT ON app.f TO [{u}_r]; ALTER ROLE [{u}_r] ADD MEMBER [{u}]", "", 1 },
        { "function_alter", "GRANT ALTER ON app.f TO [{u}]", "", 1 },
        { "procedure_execute", "GRANT EXECUTE ON app.p TO [{u}]", "", 1 },
        { "schema_execute", "GRANT EXECUTE ON SCHEMA::app TO [{u}]", "", 1 },
        { "queue_receive", "GRANT RECEIVE ON app.q TO [{u}]", "", 1 },
        { "owns_object", "CREATE TABLE app.[owned_{u}] (id int); ALTER AUTHORIZATION ON OBJECT::app.[owned_{u}] TO [{u}]", "", 1 },
        { "db_datawriter", "ALTER ROLE db_datawriter ADD MEMBER [{u}]", "", 1 },
        { "public_delete", "GRANT DELETE ON app.t TO public", "REVOKE DELETE ON app.t FROM public", 1 },
    };

    [SqlServerAdminFact]
    [MemberData(nameof(Cases))]
    public async Task The_check_refuses_exactly_the_users_that_can_change_data(string name, string setup, string teardown, int expected)
    {
        var connectionString = _database.ConnectionString!;
        var user = "tro_" + name;
        await SqlServerScratchDatabase.ExecuteAsync(connectionString, $"CREATE USER [{user}] WITHOUT LOGIN; ALTER ROLE db_datareader ADD MEMBER [{user}]");
        try
        {
            if (setup.Length > 0)
            {
                await SqlServerScratchDatabase.ExecuteAsync(connectionString, setup.Replace("{u}", user, StringComparison.Ordinal));
            }

            var result = await SqlServerScratchDatabase.ExecuteAsync(
                connectionString,
                $"EXECUTE AS USER = N'{user}'; {TabularRowReader.SqlServerVerifyReadOnlySql}; REVERT;");

            Convert.ToInt32(result).Should().Be(expected, "user {0} ({1})", user, setup.Length > 0 ? setup : "db_datareader only");
        }
        finally
        {
            if (teardown.Length > 0)
            {
                await SqlServerScratchDatabase.ExecuteAsync(connectionString, teardown);
            }
        }
    }
}
