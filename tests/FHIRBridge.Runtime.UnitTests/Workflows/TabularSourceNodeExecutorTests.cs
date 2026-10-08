using System.Text.Json;
using FHIRBridge.Application.Abstractions.Tabular;
using FHIRBridge.Application.Services.Tabular;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

public sealed class TabularSourceNodeExecutorTests
{
    private static readonly string Templates = TabularTemplatePresets.ToStoredJson(["Patient", "AllergyIntolerance"]);

    private static WorkflowNode Node(Dictionary<string, string> config) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        WorkflowNodeTypes.TabularSource,
        WorkflowNodeCategory.Source,
        0,
        0,
        "CSV / SQL Table",
        JsonSerializer.Serialize(config),
        0,
        0,
        true);

    private static Dictionary<string, string> CsvConfig(string? datasetKey = "clinic-allergies") => new()
    {
        ["tab_kind"] = "csv",
        ["tab_fileId"] = Guid.NewGuid().ToString(),
        ["tab_datasetKey"] = datasetKey ?? string.Empty,
        ["tab_templates"] = Templates,
    };

    private sealed class FakeReader : ITabularRowReader
    {
        public TabularRows Rows { get; init; } = new([], [], false);

        /// <summary>Rows per query text, for sources that read each resource type with its own query.</summary>
        public Dictionary<string, TabularRows> RowsByQuery { get; init; } = new();
        public int? LastMaxRows { get; private set; }
        public TabularSqlQuery? LastQuery { get; private set; }
        public List<string> Queries { get; } = [];
        public int FileReads { get; private set; }

        public Task<TabularRows> ReadFileAsync(Guid fileId, int maxRows, CancellationToken cancellationToken)
        {
            FileReads++;
            LastMaxRows = maxRows;
            return Task.FromResult(Rows);
        }

        public Task<TabularRows> ReadSqlAsync(TabularSqlQuery query, int maxRows, CancellationToken cancellationToken)
        {
            LastQuery = query;
            LastMaxRows = maxRows;
            Queries.Add(query.Query);
            return Task.FromResult(RowsByQuery.TryGetValue(query.Query, out var rows) ? rows : Rows);
        }

        public Task<IReadOnlyList<string>> DescribeSqlAsync(TabularSqlQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>((RowsByQuery.TryGetValue(query.Query, out var rows) ? rows : Rows).Columns);
    }

    private static string Streams(params object[] entries) => JsonSerializer.Serialize(entries);

    private static object Entry(string type, string? query = null, string? fileId = null, string? filterColumn = null, string? filterValue = null) => new
    {
        resourceType = type,
        query,
        fileId,
        rowFilterColumn = filterColumn,
        rowFilterValue = filterValue,
        template = JsonDocument.Parse(TabularTemplatePresets.All[type]).RootElement,
    };

    [Fact]
    public async Task Rows_become_a_resource_batch_with_row_errors_counted_not_shown()
    {
        var reader = new FakeReader
        {
            Rows = CsvTable.Parse(
                "patient_id,last_name,first_name,gender,birth_date,allergy_id,allergen\n"
                + "p1,Powell,Desiree,female,2014-11-14,a1,Penicillin\n"
                + "p1,Powell,Desiree,female,2014-11-14,a2,Latex\n"
                + "p2,Ross,Linda,female,not a date,a3,Peanut\n", 100),
        };

        var output = await new TabularSourceNodeExecutor(reader).ExecuteAsync(
            new WorkflowExecutionContext(Guid.NewGuid(), "corr"), Node(CsvConfig()), [], CancellationToken.None);

        output.Contract.Should().Be(WorkflowDataContract.ResourceBatch);
        var batch = output.Payload.Should().BeOfType<ResourceBatch>().Subject;
        batch.Resources.Select(r => $"{r.ResourceType}/{r.ResourceId}")
            .Should().BeEquivalentTo(["Patient/p1", "AllergyIntolerance/a1", "AllergyIntolerance/a2", "AllergyIntolerance/a3"]);
        output.Metadata!["rowsRead"].Should().Be(3);
        output.Metadata["rowErrorCount"].Should().Be(1);
        ((IReadOnlyList<string>)output.Metadata["rowErrors"]!).Single().Should().NotContain("not a date");
        reader.LastMaxRows.Should().Be(TabularSourceSettings.DefaultMaxRows);
    }

    [Fact]
    public async Task A_sql_source_reads_through_its_secret_reference()
    {
        var reader = new FakeReader();
        var config = new Dictionary<string, string>
        {
            ["tab_kind"] = "sql",
            ["tab_datasetKey"] = "warehouse-allergies",
            ["tab_sqlEngine"] = "PostgreSQL",
            ["tab_secretKeyVaultName"] = "kv",
            ["tab_secretName"] = "tabular-sql-postgresql-1",
            ["tab_query"] = "select * from allergies",
            ["tab_maxRows"] = "999999",
            ["tab_templates"] = Templates,
        };

        await new TabularSourceNodeExecutor(reader).ExecuteAsync(
            new WorkflowExecutionContext(Guid.NewGuid(), "corr"), Node(config), [], CancellationToken.None);

        reader.LastQuery!.Engine.Should().Be("postgresql");
        reader.LastQuery.ConnectionSecret.SecretName.Should().Be("tabular-sql-postgresql-1");
        reader.LastMaxRows.Should().Be(TabularSourceSettings.MaxAllowedRows);
    }

    [Theory]
    [InlineData(null, "*dataset key*")]
    [InlineData("ok-key", "*CSV file*")]
    public async Task Misconfigured_sources_fail_instead_of_looking_empty(string? datasetKey, string message)
    {
        var config = CsvConfig(datasetKey);
        if (datasetKey is not null)
        {
            config["tab_fileId"] = string.Empty;
        }

        var act = () => new TabularSourceNodeExecutor(new FakeReader()).ExecuteAsync(
            new WorkflowExecutionContext(Guid.NewGuid(), "corr"), Node(config), [], CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
    }

    [Fact]
    public void The_catalog_lists_the_tabular_source_with_its_required_keys()
    {
        var item = new DefaultWorkflowNodeCatalog().Find(WorkflowNodeTypes.TabularSource);

        item.Should().NotBeNull();
        item!.Category.Should().Be(WorkflowNodeCategory.Source);
        item.TransformId.Should().Be("tabular");
        item.RequiredConfigurationFields.Should().BeEquivalentTo([TabularSourceSettings.KindKey, TabularSourceSettings.DatasetKeyKey]);
    }

    [Fact]
    public async Task Each_resource_type_reads_its_own_query_through_its_own_template_into_one_batch()
    {
        var reader = new FakeReader
        {
            RowsByQuery = new()
            {
                ["select * from patients"] = CsvTable.Parse("patient_id,last_name,first_name,gender,birth_date\np1,Powell,Desiree,female,2014-11-14\n", 100),
                ["select * from allergies"] = CsvTable.Parse("patient_id,allergy_id,allergen\np1,a1,Penicillin\np1,a2,Latex\n", 100),
            },
        };
        var config = new Dictionary<string, string>
        {
            ["tab_kind"] = "sql",
            ["tab_datasetKey"] = "warehouse",
            ["tab_sqlEngine"] = "postgresql",
            ["tab_secretKeyVaultName"] = "kv",
            ["tab_secretName"] = "tabular-sql-postgresql-1",
            [TabularSourceSettings.StreamsKey] = Streams(
                Entry("Patient", query: "select * from patients"),
                Entry("AllergyIntolerance", query: "select * from allergies")),
        };

        var output = await new TabularSourceNodeExecutor(reader).ExecuteAsync(
            new WorkflowExecutionContext(Guid.NewGuid(), "corr"), Node(config), [], CancellationToken.None);

        reader.Queries.Should().Equal("select * from patients", "select * from allergies");
        var batch = output.Payload.Should().BeOfType<ResourceBatch>().Subject;
        batch.Resources.Select(r => $"{r.ResourceType}/{r.ResourceId}")
            .Should().Equal("Patient/p1", "AllergyIntolerance/a1", "AllergyIntolerance/a2");
        output.Metadata!["rowsRead"].Should().Be(3);
        ((IReadOnlyList<Dictionary<string, object?>>)output.Metadata["resourceTypes"]!).Select(t => t["built"]).Should().Equal(1, 2);
    }

    [Fact]
    public async Task Csv_types_sharing_one_file_read_it_once_and_keep_only_their_rows()
    {
        var fileId = Guid.NewGuid().ToString();
        var reader = new FakeReader
        {
            Rows = CsvTable.Parse(
                "record_type,patient_id,last_name,first_name,gender,birth_date,allergy_id,allergen\n"
                + "patient,p1,Powell,Desiree,female,2014-11-14,,\n"
                + "allergy,p1,,,,,a1,Penicillin\n"
                + " Allergy ,p1,,,,,a2,Latex\n", 100),
        };
        var config = new Dictionary<string, string>
        {
            ["tab_kind"] = "csv",
            ["tab_datasetKey"] = "clinic",
            [TabularSourceSettings.StreamsKey] = Streams(
                Entry("Patient", fileId: fileId, filterColumn: "record_type", filterValue: "patient"),
                Entry("AllergyIntolerance", fileId: fileId, filterColumn: "RECORD_TYPE", filterValue: "allergy")),
        };

        var output = await new TabularSourceNodeExecutor(reader).ExecuteAsync(
            new WorkflowExecutionContext(Guid.NewGuid(), "corr"), Node(config), [], CancellationToken.None);

        reader.FileReads.Should().Be(1);
        output.Payload.Should().BeOfType<ResourceBatch>().Subject.Resources.Select(r => $"{r.ResourceType}/{r.ResourceId}")
            .Should().Equal("Patient/p1", "AllergyIntolerance/a1", "AllergyIntolerance/a2");
    }

    [Fact]
    public async Task A_row_filter_on_a_column_the_file_lacks_fails_instead_of_matching_nothing()
    {
        var reader = new FakeReader { Rows = CsvTable.Parse("patient_id,allergy_id,allergen\np1,a1,Penicillin\n", 100) };
        var config = new Dictionary<string, string>
        {
            ["tab_kind"] = "csv",
            ["tab_datasetKey"] = "clinic",
            [TabularSourceSettings.StreamsKey] = Streams(
                Entry("AllergyIntolerance", fileId: Guid.NewGuid().ToString(), filterColumn: "record_type", filterValue: "allergy")),
        };

        var act = () => new TabularSourceNodeExecutor(reader).ExecuteAsync(
            new WorkflowExecutionContext(Guid.NewGuid(), "corr"), Node(config), [], CancellationToken.None);

        await act.Should().ThrowAsync<Exception>().WithMessage("*row filter column 'record_type'*");
    }
}
