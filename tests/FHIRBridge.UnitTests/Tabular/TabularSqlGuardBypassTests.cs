using FHIRBridge.Infrastructure.Tabular;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Tabular;

/// <summary>
/// PR #244 review, round 6: the query check sees every keyword as the engine does, even one written against a quoted
/// name or a number; PostgreSQL's blocked functions stay blocked when quoted; MySQL's "--" before a control character
/// is a comment to the check as it is to MySQL; and the SQL Server login check counts schema and object grants.
/// </summary>
public sealed class TabularSqlGuardBypassTests
{
    [Theory]
    // A keyword against a quoted name: the name's placeholder no longer glues them into one word.
    [InlineData("sqlserver", "SELECT 1 AS [a]DELETE[dbo].[Patients]", "*'DELETE' is not allowed*")]
    [InlineData("sqlserver", "SELECT 1 AS [a]UPDATE[t]SET[c]=1", "*'UPDATE' is not allowed*")]
    [InlineData("sqlserver", "SELECT 1 AS \"a\"EXECUTE[p]", "*'EXECUTE' is not allowed*")]
    [InlineData("sqlserver", "SELECT [a]FROM[t]WAITFOR DELAY '00:00:10'", "*'WAITFOR' is not allowed*")]
    [InlineData("postgresql", "select \"a\"into\"b\" from t", "*'INTO' is not allowed*")]
    [InlineData("mysql", "select `a`into outfile '/tmp/x'", "*'INTO' is not allowed*")]
    // A keyword against a number: SQL Server reads 1DELETE as 1 then DELETE, and 1e, 0x and $1 are numbers too.
    [InlineData("sqlserver", "SELECT a FROM t WHERE 1=1DELETE t", "*'DELETE' is not allowed*")]
    [InlineData("sqlserver", "SELECT 1DECLARE @x int", "*'DECLARE' is not allowed*")]
    [InlineData("sqlserver", "SELECT 1.5SET NOCOUNT ON", "*'SET' is not allowed*")]
    [InlineData("sqlserver", "SELECT 1eSET NOCOUNT ON", "*'SET' is not allowed*")]
    [InlineData("sqlserver", "SELECT 1e5DELETE t", "*'DELETE' is not allowed*")]
    [InlineData("sqlserver", "SELECT 0xSET NOCOUNT ON", "*'SET' is not allowed*")]
    [InlineData("sqlserver", "SELECT $1COMMIT", "*'COMMIT' is not allowed*")]
    // The reader's own transaction may not be ended from the query.
    [InlineData("sqlserver", "SELECT 1 COMMIT", "*'COMMIT' is not allowed*")]
    [InlineData("sqlserver", "SELECT 1 ROLLBACK", "*'ROLLBACK' is not allowed*")]
    [InlineData("sqlserver", "SELECT 1 SAVE TRAN x", "*'SAVE' is not allowed*")]
    [InlineData("sqlserver", "SELECT 1 AS [a]DELETE[dbo].[Patients]COMMIT", "*is not allowed*")]
    // A sign after 1e is taken only with a digit after it, so "--" there is still a comment that hides nothing.
    [InlineData("sqlserver", "select 1e--'\n; delete from t --'", "*single SELECT*")]
    public void A_keyword_glued_to_a_name_or_a_number_is_still_refused(string engine, string query, string message)
    {
        var act = () => TabularSqlGuard.ValidateQuery(query, engine);

        act.Should().Throw<BusinessRuleException>().WithMessage(message);
    }

    [Theory]
    [InlineData("postgresql", "SELECT \"pg_terminate_backend\"(pid) AS k FROM pg_stat_activity", "*'PG_TERMINATE_BACKEND' is not allowed*")]
    [InlineData("postgresql", "select \"pg_read_file\"('/etc/passwd')", "*'PG_READ_FILE' is not allowed*")]
    [InlineData("postgresql", "select pg_catalog.\"pg_sleep\"(10)", "*'PG_SLEEP' is not allowed*")]
    [InlineData("postgresql", "select \"pg_advisory_lock\"(1)", "*'PG_ADVISORY_LOCK' is not allowed*")]
    [InlineData("postgresql", "select \"lo_truncate64\"(1, 0)", "*'LO_TRUNCATE64' is not allowed*")]
    [InlineData("postgresql", "select * from \"dblink\"('db', 'delete from t') as x(a int)", "*'DBLINK' is not allowed*")]
    [InlineData("postgresql", "select \"dblink_exec\"('db', 'delete from t')", "*'DBLINK_EXEC' is not allowed*")]
    // U&"..." escapes could spell a blocked name (U&"pg\005fsleep"), so they are refused outright.
    [InlineData("postgresql", "select U&\"pg\\005fsleep\"(10)", "*U&*")]
    // PostgreSQL before 15 reads 1e'...' as 1 then an E'...' string, where a backslash escapes the quote.
    [InlineData("postgresql", "select 1e'\\' as a, ' ; drop table t; --'", "*single SELECT*")]
    public void A_postgresql_server_function_is_refused_even_quoted(string engine, string query, string message)
    {
        var act = () => TabularSqlGuard.ValidateQuery(query, engine);

        act.Should().Throw<BusinessRuleException>().WithMessage(message);
    }

    [Theory]
    // MySQL reads "--" before any ASCII control character as a comment, so the quote after it is comment text and
    // the ';' on the next line is code.
    [InlineData("mysql", "SELECT 1 --\u0001'\n; COMMIT; DELETE FROM patients; -- '")]
    [InlineData("mysql", "SELECT 1 --\u007f'\n; DELETE FROM patients; -- '")]
    [InlineData("mysql", "SELECT 1 --\u001f'\n; DELETE FROM patients; -- '")]
    // A non-ASCII space after "--" is part of a name to MySQL, so the rest of the line is code to the check too.
    [InlineData("mysql", "SELECT 1 -- ; DELETE FROM patients")]
    public void Mysql_dashes_are_a_comment_exactly_when_mysql_reads_one(string engine, string query)
    {
        var act = () => TabularSqlGuard.ValidateQuery(query, engine);

        act.Should().Throw<BusinessRuleException>().WithMessage("*single SELECT*");
    }

    [Theory]
    // Keyword-named columns in quotes, names and numbers that only look like a keyword, and ordinary numbers.
    [InlineData("sqlserver", "select [delete], [commit], [save] from t")]
    [InlineData("postgresql", "select \"call\", \"delete\", \"sleep_note\", \"my_dblink\" from t")]
    [InlineData("postgresql", "select 1e5 as n, E'a\\'b' as s from t")]
    [InlineData("mysql", "select `insert`, `call` from t")]
    [InlineData("sqlserver", "select t1delete, a1 from t where x = 1.5e-3 and y = 0x1F and z = $1.50")]
    [InlineData("sqlserver", "select [a] from [t] where [b] = 1")]
    [InlineData("mysql", "select 1--1 as n from t")]
    [InlineData("mysql", "select a from t -- a note\nwhere b = 1")]
    [InlineData("mysql", "select a from t --\ta note")]
    public void A_read_with_names_and_numbers_still_runs_as_written(string engine, string query)
    {
        TabularSqlGuard.ValidateQuery(query, engine).Should().Be(query);
    }

    [Fact]
    public void A_quoted_name_spelled_like_a_blocked_function_is_refused_even_when_not_called()
    {
        // A column named exactly like a blocked function is refused even when not called: the check cannot tell a
        // column from a call it would miss, and the name is rare.
        var act = () => TabularSqlGuard.ValidateQuery("select \"set_config\" from t", "postgresql");

        act.Should().Throw<BusinessRuleException>().WithMessage("*'SET_CONFIG' is not allowed*");
    }

    [Theory]
    // Item 19: grants on a schema, a table, view or inline function, a procedure or a queue, and ownership, which
    // HAS_PERMS_BY_NAME on the DATABASE misses. Checked against SQL Server 2019 with users WITHOUT LOGIN in a scratch
    // database of 20,000 tables and 2,000 procedures: a db_datareader user passes in under 20 ms (the per-object
    // HAS_PERMS_BY_NAME version took 6.5 s), and INSERT on an inline function through a role, RECEIVE on a queue,
    // EXECUTE on a schema or a procedure, a column UPDATE, ownership, ALTER on a function, INSERT on a schema,
    // db_datawriter and DELETE granted to public are each refused. These text checks are only a quick smoke test: what
    // the query does is checked on a real server by TabularSqlServerReadOnlyCheckTests (TABULAR_IT_SQLSERVER_ADMIN).
    [InlineData("'SCHEMA'")]
    [InlineData("sys.schemas")]
    [InlineData("so.schema_id = s.schema_id")]
    [InlineData("sys.database_permissions dp")]
    [InlineData("sys.user_token")]
    [InlineData("dp.state IN ('G', 'W')")]
    [InlineData("o.type IN ('U', 'V', 'IF')")]
    [InlineData("o.type IN ('P', 'PC', 'X')")]
    [InlineData("dp.permission_name = 'RECEIVE' AND o.type = 'SQ'")]
    [InlineData("dp.class = 3")]
    [InlineData("o.principal_id IN")]
    [InlineData("('INSERT'), ('UPDATE'), ('DELETE'), ('ALTER'), ('CONTROL'), ('TAKE OWNERSHIP')")]
    [InlineData("HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'INSERT')")]
    [InlineData("IS_SRVROLEMEMBER('sysadmin')")]
    public void The_sql_server_login_check_counts_schema_and_object_grants(string fragment)
    {
        TabularRowReader.SqlServerVerifyReadOnlySql.Should().Contain(fragment);
    }

    [Fact]
    public void The_sql_server_login_check_never_asks_per_object()
    {
        // HAS_PERMS_BY_NAME on every object is ~100k evaluations on a large catalog, before every query.
        TabularRowReader.SqlServerVerifyReadOnlySql.Should().NotContain("'OBJECT'");
    }

    [Theory]
    // Service Broker statements run as a second statement in the batch; RECEIVE deletes the queue's messages.
    [InlineData("SELECT 1 AS a RECEIVE * FROM q", "*'RECEIVE' is not allowed*")]
    [InlineData("SELECT [a]FROM[t]END CONVERSATION 'x'", "*'END CONVERSATION' is not allowed*")]
    [InlineData("SELECT [a]FROM[t]END/**/CONVERSATION 'x'", "*is not allowed*")]
    [InlineData("SELECT 1 AS a SEND ON CONVERSATION 'x' (0x)", "*'SEND' is not allowed*")]
    [InlineData("SELECT 1 AS a GET CONVERSATION GROUP 'x' FROM q", "*'GET CONVERSATION' is not allowed*")]
    [InlineData("SELECT 1 AS a MOVE CONVERSATION 'x' TO 'y'", "*'MOVE CONVERSATION' is not allowed*")]
    public void A_sql_server_service_broker_statement_is_refused(string query, string message)
    {
        var act = () => TabularSqlGuard.ValidateQuery(query, "sqlserver");

        act.Should().Throw<BusinessRuleException>().WithMessage(message);
    }

    [Theory]
    [InlineData("SELECT CASE WHEN a = 1 THEN 'x' ELSE 'y' END AS c FROM t")]
    [InlineData("SELECT CASE WHEN a = 1 THEN 1 END [conversation] FROM t")]
    [InlineData("SELECT [receive], [send], [end] FROM t")]
    public void A_case_end_or_a_quoted_broker_word_still_runs(string query)
    {
        TabularSqlGuard.ValidateQuery(query, "sqlserver").Should().Be(query);
    }
}
