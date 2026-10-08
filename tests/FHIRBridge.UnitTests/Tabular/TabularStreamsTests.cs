using System.Text.Json;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Tabular;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services.Tabular;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Tabular;

/// <summary>
/// A Tabular source that names its resource types: each type has its own query or file and template, and is checked
/// against what it reads before the source is saved, without reading a row unless a preview is asked for.
/// </summary>
public sealed class TabularStreamsTests
{
    private const string AllergyQuery = "SELECT allergy_id, patient_id, allergen FROM allergies";

    private static object Entry(string type, string? query = null, Guid? fileId = null, string? filterColumn = null, string? filterValue = null) => new
    {
        resourceType = type,
        query,
        fileId = fileId?.ToString(),
        rowFilterColumn = filterColumn,
        rowFilterValue = filterValue,
        template = JsonDocument.Parse(TabularTemplatePresets.All[type]).RootElement,
    };

    private static string Streams(params object[] entries) => JsonSerializer.Serialize(entries);

    private sealed class ScriptedReader : ITabularRowReader
    {
        public Dictionary<string, IReadOnlyList<string>> Described { get; } = new();
        public Dictionary<string, string> Refused { get; } = new();
        public TabularRows FileRows { get; init; } = new([], [], false);
        public int RowReads { get; private set; }

        public Task<TabularRows> ReadFileAsync(Guid fileId, int maxRows, CancellationToken cancellationToken)
        {
            RowReads++;
            return Task.FromResult(FileRows);
        }

        public Task<TabularRows> ReadSqlAsync(TabularSqlQuery query, int maxRows, CancellationToken cancellationToken)
        {
            RowReads++;
            return Task.FromResult(new TabularRows(Described[query.Query], [], false));
        }

        public Task<IReadOnlyList<string>> DescribeSqlAsync(TabularSqlQuery query, CancellationToken cancellationToken) =>
            Refused.TryGetValue(query.Query, out var message)
                ? throw new BusinessRuleException(message)
                : Task.FromResult(Described[query.Query]);
    }

    private sealed class PlainEncryptor : IPhiFieldEncryptor
    {
        public string Encrypt(string plaintext) => plaintext;

        public string Decrypt(string ciphertext) => ciphertext;
    }

    private static (TabularSourceService Service, InMemoryTabularSourceFileRepository Files) Create(ScriptedReader reader) =>
        Create(reader, new Mock<ISecretWriter>());

    private static (TabularSourceService Service, InMemoryTabularSourceFileRepository Files) Create(ScriptedReader reader, Mock<ISecretWriter> secrets)
    {
        var files = new InMemoryTabularSourceFileRepository();
        var user = new Mock<ICurrentUserService>();
        user.SetupGet(u => u.CurrentUser).Returns(new CurrentUserInfo("ext", "uploader@example.com", "U", [], true));
        var vaults = new Mock<ITenantSecretVaultResolver>();
        vaults.Setup(v => v.ResolveVaultName(It.IsAny<string>())).Returns<string>(name => "kv-" + name);
        var service = new TabularSourceService(
            files, reader, new PlainEncryptor(), secrets.Object, vaults.Object, user.Object,
            NullLogger<TabularSourceService>.Instance, new InMemoryTabularSqlConnectionRepository());
        return (service, files);
    }

    private static TabularCheckRequest Sql(string streams, bool preview = false) =>
        new("sql", "postgresql", "kv", "tabular-sql-postgresql-1", streams, preview);

    // ---- Parsing ----

    [Fact]
    public void Each_entry_needs_a_resource_type_whose_template_builds_that_type()
    {
        var mismatched = Streams(new { resourceType = "Condition", query = AllergyQuery, template = new { resourceType = "AllergyIntolerance", id = "{{allergy_id}}" } });

        var act = () => TabularStreams.Parse(mismatched, TabularSourceSettings.SqlKind);

        act.Should().Throw<BusinessRuleException>().WithMessage("*builds a AllergyIntolerance, not a Condition*");
    }

    [Fact]
    public void A_sql_entry_needs_a_query_and_a_csv_entry_a_file()
    {
        var sql = () => TabularStreams.Parse(Streams(Entry("Patient")), TabularSourceSettings.SqlKind);
        var csv = () => TabularStreams.Parse(Streams(Entry("Patient", query: "SELECT 1")), TabularSourceSettings.CsvKind);

        sql.Should().Throw<BusinessRuleException>().WithMessage("*SELECT query*");
        csv.Should().Throw<BusinessRuleException>().WithMessage("*CSV file*");
    }

    [Fact]
    public void A_row_filter_needs_both_a_column_and_a_value()
    {
        var act = () => TabularStreams.Parse(Streams(Entry("Patient", fileId: Guid.NewGuid(), filterColumn: "record_type")), TabularSourceSettings.CsvKind);

        act.Should().Throw<BusinessRuleException>().WithMessage("*both a column and a value*");
    }

    [Fact]
    public void A_template_missing_its_resource_type_takes_the_entrys()
    {
        var streams = TabularStreams.Parse(
            Streams(new { resourceType = "AllergyIntolerance", query = AllergyQuery, template = new { id = "{{allergy_id}}" } }),
            TabularSourceSettings.SqlKind);

        streams.Single().Template.ResourceType.Should().Be("AllergyIntolerance");
    }

    // ---- Saved databases ----

    [Fact]
    public async Task A_saved_database_is_listed_by_name_and_keeps_its_connection_string_only_as_a_secret()
    {
        var secrets = new Mock<ISecretWriter>();
        var (service, _) = Create(new ScriptedReader(), secrets);

        var saved = await service.SaveSqlConnectionAsync(new SaveTabularSqlConnectionRequest("PostgreSQL", "Host=db;Password=x", "Clinic warehouse"), CancellationToken.None);
        var list = await service.ListSqlConnectionsAsync(CancellationToken.None);

        list.Should().ContainSingle().Which.Name.Should().Be("Clinic warehouse");
        saved.Engine.Should().Be("postgresql");
        saved.SecretName.Should().StartWith("tabular-sql-postgresql-");
        secrets.Verify(s => s.WriteSecretAsync(It.Is<SecretReference>(r => r.SecretName == saved.SecretName), "Host=db;Password=x", It.IsAny<CancellationToken>()), Times.Once);
        System.Text.Json.JsonSerializer.Serialize(list).Should().NotContain("Password");
    }

    [Fact]
    public async Task Two_saved_databases_cannot_share_a_name()
    {
        var (service, _) = Create(new ScriptedReader());
        await service.SaveSqlConnectionAsync(new SaveTabularSqlConnectionRequest("mysql", "Server=a", "Billing"), CancellationToken.None);

        var act = () => service.SaveSqlConnectionAsync(new SaveTabularSqlConnectionRequest("mysql", "Server=b", "billing"), CancellationToken.None);

        await act.Should().ThrowAsync<BusinessRuleException>().WithMessage("*already exists*");
    }

    [Fact]
    public async Task Replacing_a_connection_string_rewrites_the_same_secret_so_every_workflow_follows()
    {
        var secrets = new Mock<ISecretWriter>();
        var (service, _) = Create(new ScriptedReader(), secrets);
        var saved = await service.SaveSqlConnectionAsync(new SaveTabularSqlConnectionRequest("sqlserver", "Server=old", "EHR replica"), CancellationToken.None);

        var updated = await service.UpdateSqlConnectionAsync(saved.Id!.Value, new UpdateTabularSqlConnectionRequest("EHR replica (read)", "Server=new"), CancellationToken.None);

        updated.SecretName.Should().Be(saved.SecretName);
        updated.Name.Should().Be("EHR replica (read)");
        secrets.Verify(s => s.WriteSecretAsync(It.Is<SecretReference>(r => r.SecretName == saved.SecretName), "Server=new", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Testing_a_saved_database_reports_a_login_that_can_write_without_throwing()
    {
        var reader = new ScriptedReader();
        reader.Refused["SELECT 1 AS ok"] = "This database login can change data.";
        var (service, _) = Create(reader);
        var saved = await service.SaveSqlConnectionAsync(new SaveTabularSqlConnectionRequest("sqlserver", "Server=x", "Writable"), CancellationToken.None);

        var result = await service.TestSqlConnectionAsync(saved.Id!.Value, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("can change data");
    }

    // ---- Check ----

    [Fact]
    public async Task A_query_that_names_a_missing_table_fails_with_the_databases_own_words_and_the_others_are_still_checked()
    {
        var reader = new ScriptedReader();
        reader.Refused["SELECT * FROM nope"] = "The database says: relation \"nope\" does not exist";
        reader.Described[AllergyQuery] = ["allergy_id", "patient_id", "allergen", "rxnorm_code", "onset_date", "reaction"];
        var (service, _) = Create(reader);

        var result = await service.CheckAsync(
            Sql(Streams(Entry("Patient", query: "SELECT * FROM nope"), Entry("AllergyIntolerance", query: AllergyQuery))),
            CancellationToken.None);

        result.AllPassed.Should().BeFalse();
        result.Streams[0].Problems.Should().ContainSingle().Which.Should().Contain("relation \"nope\" does not exist");
        result.Streams[1].Passed.Should().BeTrue();
        reader.RowReads.Should().Be(0, because: "checking describes a query, it never reads its rows");
    }

    [Fact]
    public async Task A_query_that_does_not_return_the_columns_its_template_reads_fails_naming_them()
    {
        var reader = new ScriptedReader();
        reader.Described[AllergyQuery] = ["allergy_id", "patient_id", "allergen"];
        var (service, _) = Create(reader);

        var result = await service.CheckAsync(Sql(Streams(Entry("AllergyIntolerance", query: AllergyQuery))), CancellationToken.None);

        var check = result.Streams.Single();
        check.Passed.Should().BeFalse();
        check.MissingColumns.Should().BeEquivalentTo(["onset_date", "reaction", "rxnorm_code"]);
        check.Problems.Single().Should().Contain("onset_date, reaction, rxnorm_code");
    }

    [Fact]
    public async Task A_csv_entry_is_checked_against_the_header_recorded_at_upload_without_decrypting_the_file()
    {
        var reader = new ScriptedReader();
        var (service, _) = Create(reader);
        var file = await service.UploadAsync("mixed.csv", "record_type,allergy_id,patient_id,allergen,rxnorm_code,onset_date,reaction\nallergy,a1,p1,Latex,,,\n", CancellationToken.None);

        var good = await service.CheckAsync(
            new TabularCheckRequest("csv", null, null, null, Streams(Entry("AllergyIntolerance", fileId: file.Id, filterColumn: "record_type", filterValue: "allergy"))),
            CancellationToken.None);
        var badFilter = await service.CheckAsync(
            new TabularCheckRequest("csv", null, null, null, Streams(Entry("AllergyIntolerance", fileId: file.Id, filterColumn: "kind", filterValue: "allergy"))),
            CancellationToken.None);
        var gone = await service.CheckAsync(
            new TabularCheckRequest("csv", null, null, null, Streams(Entry("AllergyIntolerance", fileId: Guid.NewGuid()))),
            CancellationToken.None);

        good.AllPassed.Should().BeTrue();
        reader.RowReads.Should().Be(0);
        badFilter.Streams.Single().Problems.Single().Should().Contain("row filter column 'kind'");
        gone.Streams.Single().Problems.Single().Should().Contain("no longer exists");
    }

    [Fact]
    public async Task A_preview_builds_the_first_rows_the_entry_keeps()
    {
        var reader = new ScriptedReader
        {
            FileRows = CsvTable.Parse(
                "record_type,allergy_id,patient_id,allergen,rxnorm_code,onset_date,reaction\n"
                + "patient,,p1,,,,\n"
                + "allergy,a1,p1,Latex,,,\n", 100),
        };
        var (service, _) = Create(reader);
        var file = await service.UploadAsync("mixed.csv", "record_type,allergy_id,patient_id,allergen,rxnorm_code,onset_date,reaction\n", CancellationToken.None);

        var result = await service.CheckAsync(
            new TabularCheckRequest("csv", null, null, null,
                Streams(Entry("AllergyIntolerance", fileId: file.Id, filterColumn: "record_type", filterValue: "allergy")), Preview: true),
            CancellationToken.None);

        var check = result.Streams.Single();
        check.RowsRead.Should().Be(1);
        check.Resources.Should().ContainSingle().Which.ResourceId.Should().Be("a1");
    }
}
