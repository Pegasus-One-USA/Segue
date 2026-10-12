using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services.Tabular;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Tabular;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Tabular;

/// <summary>
/// PR #240 review: a Tabular SQL source may use only a connection the Tabular form saved; the query check reads
/// literals and comments the way the engine does and never rewrites the text that runs; functions that run text on
/// another server are refused; placeholder formats ignore case and an unreadable placeholder never reaches a resource.
/// </summary>
public sealed class TabularReviewFixTests
{
    private static ITenantSecretVaultResolver Vaults()
    {
        var vaults = new Mock<ITenantSecretVaultResolver>();
        vaults.Setup(v => v.ResolveVaultName(It.IsAny<string>())).Returns<string>(name => "kv-" + name);
        return vaults.Object;
    }

    private const string SavedName = "tabular-sql-postgresql-0123456789abcdef0123456789abcdef";

    // ---- 1. The connection secret ----

    [Fact]
    public void A_connection_the_tabular_form_saved_is_accepted()
    {
        var act = () => TabularSqlGuard.RequireTabularConnectionSecret(
            new SecretReference("kv-" + TabularSourceService.SqlConnectionVaultName, SavedName), "postgresql", Vaults());

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("kv-destinations", SavedName, "postgresql")]
    [InlineData("kv-tabular-sources", "epic-private-key", "postgresql")]
    [InlineData("kv-tabular-sources", "tabular-sql-postgresql-0123456789abcdef0123456789abcdef-x", "postgresql")]
    [InlineData("kv-tabular-sources", SavedName, "sqlserver")]
    public void Any_other_secret_is_refused_before_it_is_read(string vault, string name, string engine)
    {
        var act = () => TabularSqlGuard.RequireTabularConnectionSecret(new SecretReference(vault, name), engine, Vaults());

        act.Should().Throw<BusinessRuleException>().WithMessage("*not saved from the Tabular source form*");
    }

    // ---- 2 and 3. The query ----

    [Theory]
    [InlineData("sqlserver", "select name from t where name like '%--%' and code = 'a/*b'")]
    [InlineData("sqlserver", "select [delete] from t")]
    [InlineData("postgresql", "select $$drop table t$$ as note, \"update\" from t")]
    [InlineData("mysql", "select 1--1 as n, `insert` from t")]
    [InlineData("sqlserver", "select a from t -- a trailing note")]
    [InlineData("postgresql", "select $q1$it's$q1$ as note, a$b as name from t")]
    [InlineData("postgresql", "select 1 /* outer /* inner */ still a comment */ from t")]
    [InlineData("mysql", "select /*+ MAX_EXECUTION_TIME(1000) */ a from t")]
    [InlineData("sqlserver", "select a -- a note\r\nfrom t")]
    // Round 3: a read-only function sharing a statement keyword's name, and a keyword-named column in quotes.
    [InlineData("mysql", "select truncate(amount, 2) as amount, insert(code, 1, 0, 'x') as code from t")]
    [InlineData("mysql", "select truncate (amount, 2) from t")]
    [InlineData("postgresql", "select \"call\", \"copy\" from t")]
    [InlineData("sqlserver", "select [use], [call] from t")]
    [InlineData("sqlserver", "select 1 /* outer /* inner */ still a comment */ from t")]
    public void A_read_runs_exactly_as_written(string engine, string query)
    {
        TabularSqlGuard.ValidateQuery(query, engine).Should().Be(query);
    }

    // PR #244 review: the check wrapper puts the query inside a subquery, where a closing ';' is a syntax error, and a
    // comment after it would have kept the ';' at the end of the text.
    [Theory]
    [InlineData("postgresql", "select a from t; -- note", "select a from t -- note")]
    [InlineData("mysql", "select a from t;", "select a from t")]
    [InlineData("sqlserver", "select ';' as s from t ;; ", "select ';' as s from t")]
    public void The_closing_semicolon_is_dropped_and_nothing_else(string engine, string query, string expected)
    {
        TabularSqlGuard.ValidateQuery(query, engine).Should().Be(expected);
    }

    [Theory]
    [InlineData("sqlserver", "select * from openquery(Linked, 'DELETE FROM patients')", "*'OPENQUERY' is not allowed*")]
    [InlineData("sqlserver", "select * from openrowset('SQLNCLI', 'x', 'delete from t')", "*'OPENROWSET' is not allowed*")]
    [InlineData("postgresql", "select * from dblink('db', 'delete from t') as x(a int)", "*'DBLINK' is not allowed*")]
    [InlineData("mysql", "select 'a\\''; delete from t -- '", "*single SELECT*")]
    [InlineData("postgresql", "select E'a\\''; delete from t -- '", "*single SELECT*")]
    [InlineData("sqlserver", "select 1 /* never closed", "*never closed*")]
    // PR #244 review: MySQL runs the text of /*! ... */ (MariaDB of /*M! ... */), so it is code, not a comment.
    [InlineData("mysql", "select * from t /*!INTO OUTFILE '/tmp/x'*/", "*MySQL runs their text*")]
    [InlineData("mysql", "select * from t /*!50000 , sleep(1) */", "*MySQL runs their text*")]
    [InlineData("mysql", "select * from t /*M! into outfile '/tmp/x' */", "*MySQL runs their text*")]
    // A '$' after an identifier character is part of the name (a$x$), not a dollar quote.
    [InlineData("postgresql", "select 1 as a$x$ ; drop table t; select 1 as b$x$", "*single SELECT*")]
    // Dollar-quote tags may contain digits.
    [InlineData("postgresql", "select $a1$ ' $a1$; drop table t; --'", "*single SELECT*")]
    // PostgreSQL nests block comments, so the comment below ends at the second */.
    [InlineData("postgresql", "select 1 /* /* */ ' */ ; drop table t; -- '", "*single SELECT*")]
    // Round 2: PostgreSQL and SQL Server end a line comment at a lone '\r', MySQL does not; either reading could hide
    // the ';' from the other, so a lone '\r' inside a line comment is refused on every engine.
    [InlineData("postgresql", "select 1 -- x\r; drop table t", "*carriage return*")]
    [InlineData("sqlserver", "select 1 -- x\r; drop table t", "*carriage return*")]
    [InlineData("mysql", "select 1 -- x\r' \n; drop table t; -- '", "*carriage return*")]
    [InlineData("mysql", "select 1 # x\r; drop table t", "*carriage return*")]
    // Round 3: SQL Server runs a second statement with no ';' before it.
    [InlineData("sqlserver", "select 1 backup database x to disk = 'c:\\x.bak'", "*'BACKUP' is not allowed*")]
    [InlineData("sqlserver", "select 1 dbcc checkdb", "*'DBCC' is not allowed*")]
    [InlineData("sqlserver", "select 1 kill 53", "*'KILL' is not allowed*")]
    [InlineData("sqlserver", "select 1 shutdown", "*'SHUTDOWN' is not allowed*")]
    [InlineData("sqlserver", "select 1 waitfor delay '00:10:00'", "*'WAITFOR' is not allowed*")]
    [InlineData("sqlserver", "select 1 restore database x from disk = 'y'", "*'RESTORE' is not allowed*")]
    // SQL Server nests block comments too, so this comment ends at the second */.
    [InlineData("sqlserver", "select 1 /* /* */ ' */ delete from t -- '", "*'DELETE' is not allowed*")]
    // A function call is allowed only for read-only function names; EXEC stays refused with or without '('.
    [InlineData("sqlserver", "select 1 exec('drop table t')", "*'EXEC' is not allowed*")]
    [InlineData("mysql", "select 1 from t where 1 = 0 union select * from t into outfile '/tmp/x'", "*'INTO' is not allowed*")]
    [InlineData("postgresql", "select call from t", "*put it in quotes*")]
    public void A_query_that_could_change_data_is_refused(string engine, string query, string message)
    {
        var act = () => TabularSqlGuard.ValidateQuery(query, engine);

        act.Should().Throw<BusinessRuleException>().WithMessage(message);
    }

    // ---- 4. Placeholders ----

    private static string Template(string value) =>
        new JsonArray(new JsonObject
        {
            ["resourceType"] = "Patient",
            ["template"] = new JsonObject { ["resourceType"] = "Patient", ["id"] = "{{id}}", ["birthDate"] = value },
        }).ToJsonString();

    private static readonly Dictionary<string, string?> Row = new(StringComparer.OrdinalIgnoreCase) { ["id"] = "p1", ["dob"] = "11/14/2014", ["n"] = "7" };

    [Theory]
    [InlineData("{{dob|Date}}", "2014-11-14")]
    [InlineData("{{ dob | DATE }}", "2014-11-14")]
    [InlineData("born {{dob|date}} ({{n|integer}})", "born 2014-11-14 (7)")]
    public void Formats_ignore_case_and_apply_inside_text(string placeholder, string expected)
    {
        var templates = TabularFhirTemplateEngine.ParseTemplates(Template(placeholder));

        var (resources, errors) = TabularFhirTemplateEngine.Render(templates, Row, 1);

        errors.Should().BeEmpty();
        JsonNode.Parse(resources.Single().Json)!["birthDate"]!.GetValue<string>().Should().Be(expected);
    }

    [Theory]
    [InlineData("{{dob|Dte}}", "*unknown format 'dte'*")]
    [InlineData("{{dob}", "*cannot be read*")]
    [InlineData("{{dob|date|upper}}", "*cannot be read*")]
    public void A_placeholder_that_cannot_be_read_is_refused_when_saved(string placeholder, string message)
    {
        var act = () => TabularFhirTemplateEngine.ParseTemplates(Template(placeholder));

        act.Should().Throw<BusinessRuleException>().WithMessage(message);
    }

    [Fact]
    public void A_bad_value_inside_text_is_reported_not_copied()
    {
        var templates = TabularFhirTemplateEngine.ParseTemplates(Template("visit {{n|date}}"));

        var (resources, errors) = TabularFhirTemplateEngine.Render(templates, Row, 3);

        resources.Should().BeEmpty();
        errors.Should().ContainSingle().Which.Should().Contain("Row 3").And.Contain("'n'");
    }
}
