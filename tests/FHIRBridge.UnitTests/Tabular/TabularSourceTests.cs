using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Tabular;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services.Tabular;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack.Epic;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Tabular;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Tabular;

/// <summary>
/// Phase 4: rows from a CSV or a SQL query become FHIR resources through templates, and feed the same EHR
/// write-back writer unchanged. Errors must never carry a cell value.
/// </summary>
public sealed class TabularSourceTests
{
    // ---- CSV ----

    [Fact]
    public void Csv_handles_quotes_embedded_separators_line_breaks_and_a_bom()
    {
        var csv = "﻿patient_id,note,city\r\np1,\"says \"\"hi\"\", then\nleaves\",Verona\r\n\r\np2,,\r\n";

        var table = CsvTable.Parse(csv, 100);

        table.Columns.Should().Equal("patient_id", "note", "city");
        table.Rows.Should().HaveCount(2);
        table.Rows[0]["note"].Should().Be("says \"hi\", then\nleaves");
        table.Rows[0]["CITY"].Should().Be("Verona", because: "column lookups ignore case");
        table.Rows[1]["note"].Should().BeNull();
    }

    [Fact]
    public void Csv_detects_a_semicolon_separator()
    {
        CsvTable.Parse("a;b\n1;2\n", 10).Rows.Single()["b"].Should().Be("2");
    }

    [Fact]
    public void Csv_stops_at_the_row_cap_and_says_so()
    {
        var table = CsvTable.Parse("a\n1\n2\n3\n", 2);

        table.Rows.Should().HaveCount(2);
        table.Truncated.Should().BeTrue();
    }

    [Theory]
    [InlineData("a,a\n1,2\n", "*appears more than once*")]
    [InlineData("a,b\n1,2,3\n", "Line 2 has 3 fields*")]
    [InlineData("a,b\n\"secret value,2\n", "*never closed*")]
    [InlineData("   ", "The file is empty.")]
    public void Csv_errors_name_the_line_never_the_value(string csv, string message)
    {
        var act = () => CsvTable.Parse(csv, 10);

        act.Should().Throw<BusinessRuleException>().WithMessage(message)
            .Which.Message.Should().NotContain("secret value");
    }

    // ---- Templates ----

    private const string AllergyTemplate = """
        [{"resourceType":"AllergyIntolerance","template":{
          "resourceType":"AllergyIntolerance","id":"{{allergy_id}}",
          "code":{"coding":[{"system":"http://www.nlm.nih.gov/research/umls/rxnorm","code":"{{rxnorm}}"}],"text":"{{allergen}}"},
          "patient":{"reference":"Patient/{{patient_id}}"},
          "onsetDateTime":"{{onset|date}}",
          "reaction":[{"manifestation":[{"text":"{{reaction}}"}]}]}}]
        """;

    private static Dictionary<string, string?> Row(params (string Key, string? Value)[] cells) =>
        cells.ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_template_fills_placeholders_converts_formats_and_drops_what_is_empty()
    {
        var templates = TabularFhirTemplateEngine.ParseTemplates(AllergyTemplate);

        var (resources, errors) = TabularFhirTemplateEngine.Render(
            templates, Row(("allergy_id", "a1"), ("rxnorm", "7980"), ("allergen", "Penicillin G"), ("patient_id", "p1"), ("onset", "03/15/2020"), ("reaction", null)), 1);

        errors.Should().BeEmpty();
        var allergy = JsonNode.Parse(resources.Single().Json)!.AsObject();
        resources.Single().ResourceId.Should().Be("a1");
        allergy["patient"]!["reference"]!.GetValue<string>().Should().Be("Patient/p1");
        allergy["onsetDateTime"]!.GetValue<string>().Should().Be("2020-03-15");
        allergy.ContainsKey("reaction").Should().BeFalse(because: "an element whose column is empty is left out, with its empty parents");
    }

    [Fact]
    public void Number_boolean_datetime_and_base64_formats_change_the_value_type()
    {
        var templates = TabularFhirTemplateEngine.ParseTemplates("""
            [{"resourceType":"Observation","template":{"resourceType":"Observation","id":"{{id}}",
              "valueQuantity":{"value":"{{v|number}}"},"effectiveDateTime":"{{at|datetime}}",
              "extension":[{"url":"x","valueBoolean":"{{flag|boolean}}"}],"note":[{"text":"{{n|base64}}"}]}}]
            """);

        var (resources, _) = TabularFhirTemplateEngine.Render(
            templates, Row(("id", "o1"), ("v", "36.2"), ("at", "2026-09-30 11:47:21"), ("flag", "yes"), ("n", "hi")), 1);

        var o = JsonNode.Parse(resources.Single().Json)!;
        o["valueQuantity"]!["value"]!.GetValue<decimal>().Should().Be(36.2m);
        o["effectiveDateTime"]!.GetValue<string>().Should().Be("2026-09-30T11:47:21+00:00");
        o["extension"]![0]!["valueBoolean"]!.GetValue<bool>().Should().BeTrue();
        o["note"]![0]!["text"]!.GetValue<string>().Should().Be("aGk=");
    }

    [Fact]
    public void A_bad_cell_is_an_error_naming_the_row_and_column_but_not_the_value()
    {
        var templates = TabularFhirTemplateEngine.ParseTemplates(AllergyTemplate);

        var (resources, errors) = TabularFhirTemplateEngine.Render(
            templates, Row(("allergy_id", "a1"), ("patient_id", "p1"), ("onset", "SSN 123-45-6789")), 7);

        resources.Should().BeEmpty();
        errors.Should().ContainSingle().Which.Should().Be("Row 7: column 'onset' is not a valid date for the AllergyIntolerance template.");
    }

    [Fact]
    public void A_row_that_fills_none_of_a_templates_columns_builds_nothing_for_it()
    {
        var templates = TabularFhirTemplateEngine.ParseTemplates(AllergyTemplate);

        TabularFhirTemplateEngine.Render(templates, Row(("unrelated", "x")), 1).Resources.Should().BeEmpty();
    }

    [Fact]
    public void An_invalid_fhir_id_is_refused()
    {
        var templates = TabularFhirTemplateEngine.ParseTemplates(AllergyTemplate);

        var (_, errors) = TabularFhirTemplateEngine.Render(templates, Row(("allergy_id", "a 1/x"), ("patient_id", "p1")), 2);

        errors.Should().ContainSingle().Which.Should().StartWith("Row 2: the AllergyIntolerance id is not a valid FHIR id");
    }

    [Theory]
    [InlineData("", "*at least one template*")]
    [InlineData("{not json", "*not valid JSON*")]
    [InlineData("""[{"template":{"id":"x"}}]""", "*resourceType*")]
    [InlineData("""[{"template":{"resourceType":"Patient","id":"{{id|uppercase}}"}}]""", "*unknown format 'uppercase'*")]
    public void Broken_templates_are_refused(string json, string message)
    {
        var act = () => TabularFhirTemplateEngine.ParseTemplates(json);

        act.Should().Throw<BusinessRuleException>().WithMessage(message);
    }

    [Fact]
    public void Every_preset_parses_and_builds_its_type_from_conventional_columns()
    {
        var templates = TabularFhirTemplateEngine.ParseTemplates(TabularTemplatePresets.ToStoredJson(TabularTemplatePresets.All.Keys));
        var row = Row(
            ("patient_id", "p1"), ("last_name", "Powell"), ("first_name", "Desiree"), ("gender", "Female"), ("birth_date", "2014-11-14"),
            ("allergy_id", "a1"), ("allergen", "Penicillin"), ("problem_id", "c1"), ("problem", "Asthma"),
            ("vital_id", "o1"), ("loinc_code", "29463-7"), ("value", "36.2"), ("unit", "kg"), ("taken_at", "2026-09-30T11:00:00Z"),
            ("note_id", "n1"), ("note_loinc_code", "11506-3"), ("note_text", "Seen today."));

        var (resources, errors) = TabularFhirTemplateEngine.Render(templates, row, 1);

        errors.Should().BeEmpty();
        resources.Select(r => r.ResourceType).Should().BeEquivalentTo(TabularTemplatePresets.All.Keys);
        JsonNode.Parse(resources.Single(r => r.ResourceType == "Patient").Json)!["gender"]!.GetValue<string>().Should().Be("female");
    }

    [Fact]
    public void A_patient_repeated_on_every_row_is_kept_once()
    {
        var templates = TabularFhirTemplateEngine.ParseTemplates(TabularTemplatePresets.ToStoredJson(["Patient", "AllergyIntolerance"]));
        var table = CsvTable.Parse("patient_id,last_name,first_name,gender,birth_date,allergy_id,allergen\np1,Powell,Desiree,female,2014-11-14,a1,Penicillin\np1,Powell,Desiree,female,2014-11-14,a2,Latex\n", 10);

        var built = TabularResourceBuilder.Build(table, templates);

        built.Resources.Count(r => r.ResourceType == "Patient").Should().Be(1);
        built.Resources.Count(r => r.ResourceType == "AllergyIntolerance").Should().Be(2);
        built.DuplicatesDropped.Should().Be(1);
    }

    // ---- Identity ----

    [Theory]
    [InlineData("Clinic Allergies 2026", "urn:fhirbridge:tabular:clinic-allergies-2026")]
    [InlineData("  a__b..c  ", "urn:fhirbridge:tabular:a-b-c")]
    [InlineData("ab", null)]
    [InlineData("", null)]
    public void The_dataset_key_is_the_stable_identity_that_keys_the_ledger(string key, string? expected)
    {
        TabularSourceSettings.SourceBaseUrlFor(key).Should().Be(expected);
    }

    // ---- SQL ----

    [Theory]
    [InlineData("SELECT * FROM allergies")]
    [InlineData("  with x as (select 1 as a) select a from x;  ")]
    [InlineData("select replace(name, 'a', 'b') as n, 'insert' as label from t -- drop table t")]
    public void Read_only_queries_are_accepted(string query)
    {
        var act = () => TabularRowReader.ValidateQuery(query);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("DELETE FROM allergies", "*Only a SELECT*")]
    [InlineData("select 1; drop table allergies", "*single SELECT*")]
    [InlineData("select * into copy_table from allergies", "*'INTO' is not allowed*")]
    [InlineData("select 1 /* ; */ ; update t set a = 1", "*single SELECT*")]
    [InlineData("", "*Enter the SELECT query*")]
    public void Queries_that_could_change_data_are_refused(string query, string message)
    {
        var act = () => TabularRowReader.ValidateQuery(query);

        act.Should().Throw<BusinessRuleException>().WithMessage(message);
    }

    // ---- Service ----

    private sealed class ReversingEncryptor : IPhiFieldEncryptor
    {
        public string Encrypt(string plaintext) => "enc:" + new string(plaintext.Reverse().ToArray());

        public string Decrypt(string ciphertext) => new(ciphertext["enc:".Length..].Reverse().ToArray());
    }

    private static (TabularSourceService Service, InMemoryTabularSourceFileRepository Files, Mock<ISecretWriter> Secrets) CreateService()
    {
        var files = new InMemoryTabularSourceFileRepository();
        var encryptor = new ReversingEncryptor();
        var secrets = new Mock<ISecretWriter>();
        var vaults = new Mock<ITenantSecretVaultResolver>();
        vaults.Setup(v => v.ResolveVaultName(It.IsAny<string>())).Returns<string>(name => "kv-" + name);
        var user = new Mock<ICurrentUserService>();
        user.SetupGet(u => u.CurrentUser).Returns(new CurrentUserInfo("ext", "uploader@example.com", "U", [], true));
        var reader = new TabularRowReader(files, encryptor, new Mock<ISecretProvider>().Object);
        var service = new TabularSourceService(files, reader, encryptor, secrets.Object, vaults.Object, user.Object, NullLogger<TabularSourceService>.Instance);
        return (service, files, secrets);
    }

    [Fact]
    public async Task An_uploaded_csv_is_stored_only_encrypted_and_described_without_its_content()
    {
        var (service, files, _) = CreateService();

        var dto = await service.UploadAsync("allergies.csv", "patient_id,allergen\np1,Penicillin\n", CancellationToken.None);

        dto.RowCount.Should().Be(1);
        dto.Columns.Should().Equal("patient_id", "allergen");
        var stored = await files.GetAsync(dto.Id, CancellationToken.None);
        stored!.EncryptedContent.Should().StartWith("enc:").And.NotContain("Penicillin");
        stored.CreatedBy.Should().Be("uploader@example.com");
    }

    [Fact]
    public async Task Preview_renders_the_first_rows_and_lists_columns_the_table_lacks()
    {
        var (service, _, _) = CreateService();
        var file = await service.UploadAsync("a.csv", "patient_id,allergy_id,allergen\np1,a1,Penicillin\n", CancellationToken.None);

        var preview = await service.PreviewAsync(
            new TabularPreviewRequest("csv", file.Id, null, null, null, null, AllergyTemplate), CancellationToken.None);

        preview.RowsRead.Should().Be(1);
        preview.Resources.Should().ContainSingle().Which.ResourceId.Should().Be("a1");
        preview.MissingColumns.Should().BeEquivalentTo(["onset", "reaction", "rxnorm"]);
    }

    [Fact]
    public async Task A_sql_connection_string_becomes_a_secret_and_only_its_reference_comes_back()
    {
        var (service, _, secrets) = CreateService();

        var saved = await service.SaveSqlConnectionAsync(new SaveTabularSqlConnectionRequest("PostgreSQL", "Host=db;Password=p"), CancellationToken.None);

        saved.Engine.Should().Be("postgresql");
        saved.SecretKeyVaultName.Should().Be("kv-" + TabularSourceService.SqlConnectionVaultName);
        secrets.Verify(s => s.WriteSecretAsync(
            It.Is<SecretReference>(r => r.SecretName == saved.SecretName), "Host=db;Password=p", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Oversized_or_broken_files_are_refused_at_upload()
    {
        var (service, _, _) = CreateService();

        await service.Invoking(s => s.UploadAsync("x.csv", "a,a\n1,2\n", CancellationToken.None)).Should().ThrowAsync<BusinessRuleException>();
        await service.Invoking(s => s.UploadAsync("x.csv", new string('a', TabularSourceSettings.MaxFileBytes + 1), CancellationToken.None))
            .Should().ThrowAsync<BusinessRuleException>().WithMessage("*larger than 10 MB*");
    }

    // ---- The Phase 4 gate, offline: a CSV of allergies through the unchanged EHR write-back writer ----

    [Fact]
    public async Task A_csv_of_allergies_reaches_the_ehr_write_back_writer_unchanged()
    {
        var csv = "patient_id,last_name,first_name,gender,birth_date,phone,address_line1,city,allergy_id,allergen,rxnorm_code\n"
                  + "p1,Powell,Desiree,female,2014-11-14,608-555-0142,1 Probe Way,Verona,a1,Penicillin G,7980\n";
        var templates = TabularFhirTemplateEngine.ParseTemplates(TabularTemplatePresets.ToStoredJson(["Patient", "AllergyIntolerance"]));
        var built = TabularResourceBuilder.Build(CsvTable.Parse(csv, 100), templates);
        var records = built.Resources
            .Select(r => new MappedDestinationRecord(Guid.NewGuid(), r.ResourceType, r.ResourceType, r.ResourceId, new Dictionary<string, object?>(), r.Json))
            .ToList();
        var channel = new MatchingChannel();
        var writer = new MappedEhrWriteBackDestinationWriter(
            new EhrWriteProfileRegistry([new EpicAllergyIntoleranceWriteProfile(), new EpicPatientWriteProfile()]),
            new InMemoryEhrWriteLedgerRepository(),
            Mock.Of<IEhrWriteReleasePolicy>(),
            NullLogger<MappedEhrWriteBackDestinationWriter>.Instance);

        var result = await writer.WriteAsync(
            new DestinationConfiguration("Epic", DestinationType.EhrWriteBack, new SecretReference("kv", "s"), null, "{}"),
            new MappingProfile("EHR", "Patient", Guid.NewGuid(), Guid.NewGuid(), "Patient", []),
            records,
            new PipelineWriteContext(false, "Workflow", DateTimeOffset.UtcNow,
                SourceBaseUrl: TabularSourceSettings.SourceBaseUrlFor("clinic-allergies"), EhrWriteChannel: channel),
            CancellationToken.None);

        channel.MatchCalls.Should().Be(1, because: "the CSV patient is matched to the EHR by demographics");
        result.EhrWrite!.Resources.Single(r => r.ResourceType == "AllergyIntolerance").WouldWrite.Should().Be(1);
    }

    private sealed class MatchingChannel : IEhrWriteChannel
    {
        public Guid TargetConnectionId { get; } = Guid.NewGuid();
        public SourceSystemType TargetVendor => SourceSystemType.Epic;
        public string TargetBaseUrl => "https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4";
        public Guid? DestinationId => null;
        public EhrWriteBackRunOptions Options { get; } = new(true, false, 500, "preliminary", ["Patient", "AllergyIntolerance"]);
        public int MatchCalls { get; private set; }

        public Task<string?> GetGrantedScopeAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<EhrSearchOutcome> SearchByIdentifierAsync(string resourceType, string system, string value, CancellationToken cancellationToken) =>
            Task.FromResult(new EhrSearchOutcome(true, 200, [], []));

        public Task<EhrSearchOutcome> SearchForPatientAsync(string resourceType, string targetPatientId, CancellationToken cancellationToken) =>
            Task.FromResult(new EhrSearchOutcome(true, 200, [], []));

        public Task<EhrPatientMatchOutcome> MatchPatientAsync(string patientJson, CancellationToken cancellationToken)
        {
            MatchCalls++;
            return Task.FromResult(new EhrPatientMatchOutcome(EhrPatientMatchKind.Certain, "eTarget", 200, []));
        }

        public Task<EhrCreateOutcome> CreateAsync(string resourceType, string resourceJson, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A dry run must not create.");
    }
}
