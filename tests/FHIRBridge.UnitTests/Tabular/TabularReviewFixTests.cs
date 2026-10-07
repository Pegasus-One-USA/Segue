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
    public void A_read_runs_exactly_as_written(string engine, string query)
    {
        TabularSqlGuard.ValidateQuery(query, engine).Should().Be(query);
    }

    [Theory]
    [InlineData("sqlserver", "select * from openquery(Linked, 'DELETE FROM patients')", "*'OPENQUERY' is not allowed*")]
    [InlineData("sqlserver", "select * from openrowset('SQLNCLI', 'x', 'delete from t')", "*'OPENROWSET' is not allowed*")]
    [InlineData("postgresql", "select * from dblink('db', 'delete from t') as x(a int)", "*'DBLINK' is not allowed*")]
    [InlineData("mysql", "select 'a\\''; delete from t -- '", "*single SELECT*")]
    [InlineData("postgresql", "select E'a\\''; delete from t -- '", "*single SELECT*")]
    [InlineData("sqlserver", "select 1 /* never closed", "*never closed*")]
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
