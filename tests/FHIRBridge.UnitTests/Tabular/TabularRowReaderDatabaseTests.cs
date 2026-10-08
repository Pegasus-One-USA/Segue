using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Tabular;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Tabular;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Tabular;

/// <summary>Runs only when <paramref name="variable"/> holds a read-only connection string; skipped otherwise.</summary>
public sealed class TabularDatabaseFactAttribute : TheoryAttribute
{
    public TabularDatabaseFactAttribute()
    {
        if (TabularRowReaderDatabaseTests.Engines.All(e => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(e.Variable))))
        {
            Skip = "Set TABULAR_IT_POSTGRESQL, TABULAR_IT_MYSQL and/or TABULAR_IT_SQLSERVER to a read-only login on a throwaway database.";
        }
    }
}

/// <summary>
/// The Tabular reader against real databases: checking a query names a missing table or column in the database's own
/// words and returns no row; reading still works. Each engine runs only when its variable is set, with a read-only
/// login on a THROWAWAY database (never FHIRBridge_v2) holding one table:
/// <c>allergies(allergy_id, patient_id, allergen)</c> with one made-up row.
/// </summary>
public sealed class TabularRowReaderDatabaseTests
{
    public static readonly (string Engine, string Variable)[] Engines =
    [
        ("postgresql", "TABULAR_IT_POSTGRESQL"),
        ("mysql", "TABULAR_IT_MYSQL"),
        ("sqlserver", "TABULAR_IT_SQLSERVER"),
    ];

    public static TheoryData<string> ConfiguredEngines()
    {
        var data = new TheoryData<string>();
        foreach (var (engine, variable) in Engines.Where(e => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(e.Variable))))
        {
            data.Add(engine);
        }

        return data;
    }

    private static (TabularRowReader Reader, Func<string, TabularSqlQuery> Query) Create(string engine)
    {
        var connectionString = Environment.GetEnvironmentVariable(Engines.Single(e => e.Engine == engine).Variable)!;
        var secret = new SecretReference("kv-tabular-sources", $"tabular-sql-{engine}-{Guid.NewGuid():N}");
        var secrets = new Mock<ISecretProvider>();
        secrets.Setup(s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>())).ReturnsAsync(connectionString);
        var vaults = new Mock<ITenantSecretVaultResolver>();
        vaults.Setup(v => v.ResolveVaultName(It.IsAny<string>())).Returns<string>(name => "kv-" + name);
        var reader = new TabularRowReader(new InMemoryTabularSourceFileRepository(), Mock.Of<IPhiFieldEncryptor>(), secrets.Object, vaults.Object);
        return (reader, sql => new TabularSqlQuery(engine, secret, sql));
    }

    [TabularDatabaseFact]
    [MemberData(nameof(ConfiguredEngines))]
    public async Task Checking_a_query_returns_its_columns_without_a_row(string engine)
    {
        var (reader, query) = Create(engine);

        var columns = await reader.DescribeSqlAsync(query("SELECT allergy_id, patient_id AS pid, allergen FROM allergies"), CancellationToken.None);

        columns.Should().Equal("allergy_id", "pid", "allergen");
    }

    [TabularDatabaseFact]
    [MemberData(nameof(ConfiguredEngines))]
    public async Task A_missing_table_is_named_by_the_database(string engine)
    {
        var (reader, query) = Create(engine);

        var act = () => reader.DescribeSqlAsync(query("SELECT * FROM no_such_table"), CancellationToken.None);

        (await act.Should().ThrowAsync<BusinessRuleException>()).Which.Message.Should().Contain("no_such_table");
    }

    [TabularDatabaseFact]
    [MemberData(nameof(ConfiguredEngines))]
    public async Task A_missing_column_is_named_by_the_database(string engine)
    {
        var (reader, query) = Create(engine);

        var act = () => reader.DescribeSqlAsync(query("SELECT allergy_id, no_such_column FROM allergies"), CancellationToken.None);

        (await act.Should().ThrowAsync<BusinessRuleException>()).Which.Message.Should().Contain("no_such_column");
    }

    [TabularDatabaseFact]
    [MemberData(nameof(ConfiguredEngines))]
    public async Task A_complex_query_with_a_cte_and_a_join_is_checked_and_read(string engine)
    {
        var (reader, query) = Create(engine);
        const string Sql = """
            WITH latest AS (SELECT patient_id, MAX(allergy_id) AS allergy_id FROM allergies GROUP BY patient_id)
            SELECT a.allergy_id, a.patient_id, a.allergen FROM allergies a JOIN latest l ON l.allergy_id = a.allergy_id
            """;

        var columns = await reader.DescribeSqlAsync(query(Sql), CancellationToken.None);
        var rows = await reader.ReadSqlAsync(query(Sql), 10, CancellationToken.None);

        columns.Should().Equal("allergy_id", "patient_id", "allergen");
        rows.Rows.Should().ContainSingle().Which["allergen"].Should().Be("Latex");
    }

    [TabularDatabaseFact]
    [MemberData(nameof(ConfiguredEngines))]
    public async Task A_query_that_changes_data_is_refused_before_it_reaches_the_database(string engine)
    {
        var (reader, query) = Create(engine);

        var act = () => reader.DescribeSqlAsync(query("DELETE FROM allergies"), CancellationToken.None);

        await act.Should().ThrowAsync<BusinessRuleException>().WithMessage("*SELECT*");
    }
}
