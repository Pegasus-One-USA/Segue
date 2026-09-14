using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Persistence;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Configuration;

/// <summary>
/// Covers the mapping-config import algorithm (<c>MappingImport-API-Spec.md</c> section 3): rank ordering
/// across resourceTypes, processingOrder step ordering within one resourceType, all three column modes, the
/// columnsToAdd-vs-tablesToCreate branch, and idempotent re-runs. Uses a real
/// <see cref="InMemoryConfigurationRepository"/> for the profile-upsert path and an in-memory
/// <see cref="RecordingSchemaProvider"/> fake (rather than Moq) for schema mutations, so call *order* can be
/// asserted directly instead of fighting Moq's sequence-verification API.
/// </summary>
public sealed class MappingImportServiceTests
{
    private sealed class FakeFhirElementCatalog : IFhirElementCatalog
    {
        private readonly Dictionary<string, IReadOnlyList<FhirElementDto>> _fieldsByResourceType;
        public FakeFhirElementCatalog(Dictionary<string, IReadOnlyList<FhirElementDto>> fieldsByResourceType) => _fieldsByResourceType = fieldsByResourceType;
        public string FhirVersion => "4.0.1";
        public IReadOnlyList<string> ResourceTypes => _fieldsByResourceType.Keys.ToList();
        public IReadOnlyList<FhirElementDto> Fields(string resourceType) =>
            _fieldsByResourceType.TryGetValue(resourceType, out var fields) ? fields : [];
    }

    private static (IMappingImportService Service, InMemoryConfigurationRepository Repository,
        RecordingSchemaProvider Provider, Guid DestinationId, Guid SourceConnectionId) CreateSut(
        IReadOnlyList<string> existingDestinationTables,
        IFhirElementCatalog? fhirElementCatalog = null,
        IReadOnlyDictionary<string, IReadOnlyList<DestinationColumnSchemaDto>>? existingColumnsByTable = null)
    {
        var repository = new InMemoryConfigurationRepository(TestHelpers.LicenseTestScopeFactory.Create());
        var destination = new DestinationConfiguration(
            "Test SQL Destination", DestinationType.SqlServer, new SecretReference("kv", "secret"), "FHIRBridge");
        repository.AddDestinationAsync(destination, CancellationToken.None).GetAwaiter().GetResult();

        var destinationSchemaServiceMock = new Mock<IDestinationSchemaService>();
        destinationSchemaServiceMock
            .Setup(x => x.GetSchemaAsync(destination.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DestinationSchemaDto(
                destination.Id,
                existingDestinationTables.Select(t => new DestinationTableSchemaDto(
                    "dbo", t, $"dbo.{t}",
                    existingColumnsByTable is not null && existingColumnsByTable.TryGetValue(t, out var columns)
                        ? columns
                        : [])).ToList()));

        var provider = new RecordingSchemaProvider();
        var factory = new Mock<IMappingSchemaProviderFactory>();
        factory.Setup(x => x.Create(DestinationType.SqlServer)).Returns(provider);

        var service = new MappingImportService(
            repository,
            destinationSchemaServiceMock.Object,
            factory.Object,
            fhirElementCatalog);

        return (service, repository, provider, destination.Id, Guid.NewGuid());
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>Stamps <c>existingMappingProfileId</c> onto every entry in "mappings" — the same field the real
    /// wizard save flow round-trips from a prior import's response (see ResourceMappingDto.ExistingMappingProfileId)
    /// so a re-import updates the caller's own profile instead of creating a new one.</summary>
    private static JsonElement WithExistingMappingProfileId(string json, Guid existingMappingProfileId)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        foreach (var mapping in node["mappings"]!.AsArray())
        {
            mapping!.AsObject()["existingMappingProfileId"] = existingMappingProfileId.ToString();
        }

        return JsonDocument.Parse(node.ToJsonString()).RootElement;
    }

    [Fact]
    public async Task Import_creates_new_tables_adds_columns_and_persists_all_column_modes()
    {
        var (service, repository, provider, destinationId, sourceConnectionId) =
            CreateSut(existingDestinationTables: ["Patient"]);
        provider.LiveColumnTypes[("Patient", "Id")] = "bigint";

        var body = FullPatientFixture(sourceConnectionId, destinationId);
        var result = await service.ImportAsync(Parse(body), CancellationToken.None);

        result.Profiles.Should().HaveCount(1);
        var patientResult = result.Profiles[0];
        patientResult.ResourceType.Should().Be("Patient");
        // PatientContactRelation and PatientAddCombo are genuinely separate (non-root) tables with repeating
        // fields but no declared `relation` back to Patient — the import surfaces that as a warning rather
        // than silently mis-linking their rows to the root table (see ResolveArrayMetadata).
        patientResult.Warnings.Should().HaveCount(2);
        patientResult.Warnings.Should().Contain(w => w.Contains("PatientContactRelation"));
        patientResult.Warnings.Should().Contain(w => w.Contains("PatientAddCombo"));
        patientResult.TablesCreated.Should().BeEquivalentTo(
            ["PatientName", "PatientTelecom", "PatientAddres", "PatientPractitioner", "PatientContactRelation", "PatientAddCombo"]);
        patientResult.TablesSkippedAlreadyExisted.Should().BeEmpty();
        patientResult.ColumnsAdded.Should().BeEquivalentTo(
            [new ColumnAddedDto("Patient", "DeceasedBoolean"), new ColumnAddedDto("Patient", "Active")]);
        patientResult.FieldsInserted.Should().Be(23); // Patient(3) + PatientName(4) + PatientTelecom(4) + PatientAddres(7) + PatientPractitioner(3) + PatientContactRelation(1) + PatientAddCombo(1)

        var profiles = await repository.GetMappingProfilesAsync(CancellationToken.None);
        profiles.Should().HaveCount(1);
        var profile = profiles[0];
        profile.DestinationObject.Should().Be("Patient");
        profile.MappingJson.Should().NotBeNullOrWhiteSpace();
        profile.MappingJson.Should().Contain("\"resourceType\"");

        // directField — resourceType prefix stripped, "$." prefixed so JsonMappingEngine can resolve it
        var idField = profile.Fields.Single(f => f.DestinationObject == "Patient" && f.TargetField == "Id");
        idField.JsonPath.Should().Be("$.id");
        idField.Format.Should().Be("directField");
        idField.ValueType.Should().Be(MappingValueType.Integer); // resolved live via GetColumnDataTypeAsync

        // columnsToAdd branch → ValueType resolved from the columnsToAdd dataType, case-insensitively ("Bit")
        var deceasedField = profile.Fields.Single(f => f.TargetField == "DeceasedBoolean");
        deceasedField.ValueType.Should().Be(MappingValueType.Boolean);

        // directField + a nested-array "csv" aggregate, on a genuine child (FK) table → SeparateDestination.
        // arrayContext "Patient.name" (stripped: "name") splices [*] onto that segment only.
        var givenField = profile.Fields.Single(f => f.DestinationObject == "PatientName" && f.TargetField == "given");
        givenField.JsonPath.Should().Be("$.name[*].given");
        givenField.Format.Should().Be("directField;aggregate=csv");
        givenField.ArrayPolicy.Should().Be(ArrayPolicy.SeparateDestination);
        givenField.Cardinality.Should().Be("OneToMany");
        givenField.ArrayAncestors.Should().Be("Patient.name");
        givenField.ParentTable.Should().Be("Patient");
        givenField.ParentKeyColumn.Should().Be("Id");
        givenField.ForeignKeyColumn.Should().Be("PatientId");

        // joinedFields, on a table with NO parent relation → SeparateDestination + import warning (not the
        // old, incorrect RepeatParent — that would silently fold these values onto extra root-table rows).
        // Each '|'-joined sub-path independently gets [*] spliced at its own arrayContext boundary ("contact").
        var comboField = profile.Fields.Single(f => f.DestinationObject == "PatientAddCombo");
        comboField.JsonPath.Should().Be(
            "$.contact[*].address.city|$.contact[*].address.district|$.contact[*].address.state|$.contact[*].address.postalCode");
        comboField.Format.Should().Be("joinedFields;delimiter=,");
        comboField.ArrayPolicy.Should().Be(ArrayPolicy.SeparateDestination);
        comboField.ParentTable.Should().BeNull();
        comboField.ForeignKeyColumn.Should().BeNull();

        // wholeNodeAsJson — arrayContext "Patient.contact.relationship" spans TWO repeating segments
        // (contact is repeating, and each contact's relationship is itself repeating), so both get [*].
        var relationField = profile.Fields.Single(f => f.DestinationObject == "PatientContactRelation");
        relationField.JsonPath.Should().Be("$.contact[*].relationship[*]");
        relationField.Format.Should().Be("wholeNodeAsJson");
        relationField.ArrayPolicy.Should().Be(ArrayPolicy.SeparateDestination);
    }

    /// <summary>
    /// Mapping the WHOLE fetched payload as JSON into one column (a wholeNodeAsJson row whose sourceNode is
    /// the resource's own root node, which the wizard names after the resourceType itself): the stored JsonPath
    /// must be "$" — the only path <c>JsonMappingEngine.ResolveAll</c> reads as "the whole document" — and the
    /// field must come out required when the destination declares that column NOT NULL, matching the gate
    /// <see cref="Application.Validation.CreateMappingProfileRequestValidator"/> applies to the other path that
    /// writes these same profiles (the workflow save).
    /// </summary>
    [Fact]
    public async Task Import_maps_the_whole_payload_root_to_the_document_path_and_marks_a_NOT_NULL_column_required()
    {
        var (service, repository, _, destinationId, sourceConnectionId) = CreateSut(
            existingDestinationTables: ["Patient"],
            existingColumnsByTable: new Dictionary<string, IReadOnlyList<DestinationColumnSchemaDto>>
            {
                ["Patient"] =
                [
                    new DestinationColumnSchemaDto("content", "text", "String", IsNullable: false, MaxLength: null),
                    new DestinationColumnSchemaDto("gender", "text", "String", IsNullable: true, MaxLength: null),
                ],
            });

        var body = $$"""
            {
              "source": "Athena",
              "destination": "postgres",
              "sourceConnectionId": "{{sourceConnectionId}}",
              "destinationId": "{{destinationId}}",
              "mappings": [
                {
                  "resourceType": "Patient",
                  "rank": 0,
                  "generatedAt": "2026-09-10T00:00:00Z",
                  "schemaChanges": { "tablesToCreate": [], "columnsToAdd": [], "summary": null },
                  "processingOrder": [ { "step": 1, "table": "Patient", "level": 1, "dependsOn": null, "note": null } ],
                  "tables": [
                    {
                      "name": "Patient",
                      "isNew": false,
                      "relation": null,
                      "columns": [
                        { "column": "content", "mode": "wholeNodeAsJson", "sourceNode": "Patient", "instance": null },
                        { "column": "gender", "mode": "directField", "sources": ["Patient.gender"], "instance": null }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        var result = await service.ImportAsync(Parse(body), CancellationToken.None);

        result.Profiles.Should().HaveCount(1);
        result.Profiles[0].Warnings.Should().BeEmpty();

        var profile = (await repository.GetMappingProfilesAsync(CancellationToken.None)).Single();
        var contentField = profile.Fields.Single(f => f.TargetField == "content");
        contentField.JsonPath.Should().Be("$", "only the bare $ path resolves to the whole source document");
        contentField.Format.Should().Be("wholeNodeAsJson");
        contentField.IsRequired.Should().BeTrue("the destination declares 'content' NOT NULL");

        // A nullable column keeps the old default, so nothing else about an ordinary mapping changes.
        profile.Fields.Single(f => f.TargetField == "gender").IsRequired.Should().BeFalse();
    }

    [Fact]
    public async Task Import_processes_resourceTypes_by_rank_not_by_payload_order()
    {
        var (service, _, provider, destinationId, sourceConnectionId) =
            CreateSut(existingDestinationTables: ["A", "B"]);

        // "B" (rank 2, depends on a table only "A" creates) appears FIRST in the payload array —
        // proves ordering is driven by Rank, not array position.
        var body = RankOrderingFixture(sourceConnectionId, destinationId);
        var result = await service.ImportAsync(Parse(body), CancellationToken.None);

        result.Profiles.Should().HaveCount(2);
        result.Profiles[0].ResourceType.Should().Be("A");
        result.Profiles[1].ResourceType.Should().Be("B");
        result.Profiles[0].Warnings.Should().BeEmpty();
        result.Profiles[1].Warnings.Should().BeEmpty("B's dependsOn=\"SharedLookup\" must resolve against a table A already created");
        result.Profiles[0].TablesCreated.Should().BeEquivalentTo(["SharedLookup"]);
        result.Profiles[1].TablesCreated.Should().BeEquivalentTo(["BChild"]);
        provider.CreatedTables.Should().Equal("SharedLookup", "BChild");
    }

    [Fact]
    public async Task Import_creates_tables_in_processingOrder_step_order_not_tablesToCreate_array_order()
    {
        var (service, _, provider, destinationId, sourceConnectionId) =
            CreateSut(existingDestinationTables: []);

        // tablesToCreate lists the child ("CChild") before its parent ("CParent"); processingOrder.Step
        // is what must govern creation order.
        var body = StepOrderingFixture(sourceConnectionId, destinationId);
        var result = await service.ImportAsync(Parse(body), CancellationToken.None);

        result.Profiles.Single().Warnings.Should().BeEmpty();
        provider.CreatedTables.Should().Equal("CParent", "CChild");

        // CParent's own PK column ("Id") is explicitly mapped from "C.parent.id" — it must be passed through
        // as "explicitly mapped" so SqlServerMappingSchemaTransaction skips IDENTITY on it (an explicit insert
        // value would otherwise be rejected/ignored by a true identity column). CChild's PK ("Id") has no
        // field mapped to it (only its FK "ParentId" does), so it must NOT appear in that set.
        provider.ExplicitlyMappedColumnsByTable["CParent"].Should().Contain("Id");
        provider.ExplicitlyMappedColumnsByTable["CChild"].Should().Contain("ParentId").And.NotContain("Id");
    }

    [Fact]
    public async Task A_mid_resourceType_DDL_failure_rolls_back_tables_already_created_for_that_resourceType()
    {
        var (service, repository, provider, destinationId, sourceConnectionId) =
            CreateSut(existingDestinationTables: []);

        // CParent (step 2) creates successfully; CChild (step 3) then fails — CParent must not survive on its
        // own with no MappingProfile referencing it and no CChild ever created.
        provider.FailOnCreateTable.Add("CChild");

        var body = StepOrderingFixture(sourceConnectionId, destinationId);
        var result = await service.ImportAsync(Parse(body), CancellationToken.None);

        var profileResult = result.Profiles.Single();
        profileResult.Warnings.Should().ContainMatch("*Simulated DDL failure creating 'CChild'*");
        profileResult.MappingProfileId.Should().Be(Guid.Empty);

        provider.CreatedTables.Should().BeEmpty(
            "CParent was created before CChild failed, but the whole resourceType's schema transaction must roll back together");

        var profiles = await repository.GetMappingProfilesAsync(CancellationToken.None);
        profiles.Should().BeEmpty("no MappingProfile should be persisted for a resourceType whose schema changes failed");
    }

    [Fact]
    public async Task Reimporting_the_same_payload_is_idempotent_and_updates_the_existing_profile()
    {
        var (service, repository, provider, destinationId, sourceConnectionId) =
            CreateSut(existingDestinationTables: ["Patient"]);
        provider.LiveColumnTypes[("Patient", "Id")] = "bigint";

        var body = Parse(FullPatientFixture(sourceConnectionId, destinationId));

        var first = await service.ImportAsync(body, CancellationToken.None);
        var firstProfileId = first.Profiles[0].MappingProfileId;

        // Idempotency is no longer implicit (re-posting the same resourceType/source/destination triple used
        // to silently find-and-reuse whatever profile already matched it — the exact mechanism that let one
        // workflow's re-import overwrite a DIFFERENT workflow's profile sharing that triple). The caller must
        // now round-trip the id the first call returned, same as the real wizard save flow does — see
        // ResourceMappingDto.ExistingMappingProfileId.
        var secondBody = WithExistingMappingProfileId(FullPatientFixture(sourceConnectionId, destinationId), firstProfileId);

        var second = await service.ImportAsync(secondBody, CancellationToken.None);
        var secondResult = second.Profiles[0];

        secondResult.MappingProfileId.Should().Be(firstProfileId);
        secondResult.TablesCreated.Should().BeEmpty();
        secondResult.TablesSkippedAlreadyExisted.Should().BeEquivalentTo(
            ["PatientName", "PatientTelecom", "PatientAddres", "PatientPractitioner", "PatientContactRelation", "PatientAddCombo"]);
        secondResult.ColumnsAdded.Should().BeEmpty();
        secondResult.ColumnsSkippedAlreadyExisted.Should().BeEquivalentTo(
            [new ColumnAddedDto("Patient", "DeceasedBoolean"), new ColumnAddedDto("Patient", "Active")]);

        var profiles = await repository.GetMappingProfilesAsync(CancellationToken.None);
        profiles.Should().HaveCount(1, "re-importing must update the existing profile, not insert a duplicate");
    }

    /// <summary>
    /// Covers the wizard-authored "reference lookup" wiring: a column can now carry a "referenceLookup"
    /// object (table + keyColumn) alongside its normal directField mapping, so a FHIR reference field (e.g.
    /// "$.subject.reference") gets resolved against another mapped resource's own table/id column at write
    /// time instead of being written verbatim (which a bigint FK column can never accept as-is). Without
    /// this wiring, any such field previously had to be patched onto the profile by hand after every import,
    /// and a later re-import (which fully replaces a profile's fields) would silently wipe it out again.
    /// </summary>
    [Fact]
    public async Task Import_wires_a_columns_referenceLookup_onto_the_resulting_MappingField()
    {
        var (service, repository, _, destinationId, sourceConnectionId) =
            CreateSut(existingDestinationTables: ["Observation"]);

        var body = ReferenceLookupFixture(sourceConnectionId, destinationId);
        var result = await service.ImportAsync(Parse(body), CancellationToken.None);

        result.Profiles.Single().Warnings.Should().BeEmpty();
        var profile = (await repository.GetMappingProfilesAsync(CancellationToken.None)).Single();
        var patientIdField = profile.Fields.Single(f => f.TargetField == "PatientId");
        patientIdField.JsonPath.Should().Be("$.subject.reference");
        patientIdField.ReferenceLookupTable.Should().Be("Patient");
        patientIdField.ReferenceLookupKeyColumn.Should().Be("PatientId");

        // A column with no "referenceLookup" property at all must not spuriously pick one up.
        var idField = profile.Fields.Single(f => f.TargetField == "Id");
        idField.ReferenceLookupTable.Should().BeNull();
        idField.ReferenceLookupKeyColumn.Should().BeNull();
    }

    private static string ReferenceLookupFixture(Guid sourceConnectionId, Guid destinationId) => $$"""
        {
          "source": "EPIC", "destination": "SQL",
          "sourceConnectionId": "{{sourceConnectionId}}", "destinationId": "{{destinationId}}",
          "mappings": [
            {
              "resourceType": "Observation", "rank": 1, "generatedAt": "2026-07-21T16:10:52.564Z",
              "schemaChanges": { "tablesToCreate": [], "columnsToAdd": [], "summary": null },
              "processingOrder": [
                { "step": 1, "table": "Observation", "level": 1, "dependsOn": null, "note": null }
              ],
              "destination": "SQL",
              "tables": [
                { "name": "Observation", "isNew": false, "relation": null, "columns": [
                  { "column": "Id", "mode": "directField", "sources": ["Observation.id"], "instance": null },
                  {
                    "column": "PatientId", "mode": "directField", "sources": ["Observation.subject.reference"],
                    "instance": null, "referenceLookup": { "table": "Patient", "keyColumn": "PatientId" }
                  }
                ] }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task Falls_back_to_the_tables_real_foreign_key_when_the_payload_relation_is_null()
    {
        var (service, repository, provider, destinationId, sourceConnectionId) = CreateSut(existingDestinationTables: []);
        // Simulates a child table an earlier import already created correctly (real FK in the destination),
        // but whose relation THIS payload simply omits — e.g. a UI re-mapping that only carries per-column
        // sources without table-level DDL metadata for a table it now considers already-existing.
        provider.LiveForeignKeys["DChild"] = new TableRelationDto("ParentId", "dbo.D", "Id");

        var body = RelationFallbackFixture(sourceConnectionId, destinationId);
        var result = await service.ImportAsync(Parse(body), CancellationToken.None);

        var profileResult = result.Profiles.Single();
        profileResult.Warnings.Should().BeEmpty(
            "the table's real FK was discovered in the destination, so this isn't actually a relation-less table");

        var profiles = await repository.GetMappingProfilesAsync(CancellationToken.None);
        var childField = profiles.Single().Fields.Single(f => f.DestinationObject == "DChild");
        childField.ArrayPolicy.Should().Be(ArrayPolicy.SeparateDestination);
        childField.ForeignKeyColumn.Should().Be("ParentId");
        childField.ParentKeyColumn.Should().Be("Id");
        childField.ParentTable.Should().Be("dbo.D");
    }

    [Fact]
    public async Task A_renamed_root_table_is_recognized_as_the_root_not_collapsed_to_the_resourceType_name()
    {
        // Regression guard: renaming the root table (e.g. to avoid two Patient mappings colliding on the same
        // destination) must actually take effect — ResolveDestinationObject must not require the root step's
        // table NAME to equal the resourceType, only that it's the level-1, no-dependency step.
        var (service, repository, _, destinationId, sourceConnectionId) = CreateSut(existingDestinationTables: ["PatientV2"]);

        var body = RenamedRootTableFixture(sourceConnectionId, destinationId);
        var result = await service.ImportAsync(Parse(body), CancellationToken.None);

        var profileResult = result.Profiles.Single();
        profileResult.Warnings.Should().BeEmpty();

        var profiles = await repository.GetMappingProfilesAsync(CancellationToken.None);
        profiles.Single().DestinationObject.Should().Be("PatientV2");
    }

    private static string DefaultValueColumnFixture(Guid sourceConnectionId, Guid destinationId) => $$"""
        {
          "source": "EPIC", "destination": "SQL",
          "sourceConnectionId": "{{sourceConnectionId}}", "destinationId": "{{destinationId}}",
          "mappings": [
            {
              "resourceType": "Patient", "rank": 0, "generatedAt": "2026-09-14T10:00:00.000Z",
              "schemaChanges": { "tablesToCreate": [], "columnsToAdd": [], "summary": null },
              "processingOrder": [
                { "step": 1, "table": "Patient", "level": 1, "dependsOn": null, "note": null }
              ],
              "destination": "SQL",
              "tables": [
                { "name": "Patient", "isNew": false, "relation": null, "columns": [
                  { "column": "Id", "mode": "directField", "sources": ["Patient.id"], "instance": null },
                  { "column": "ClientType", "mode": "default", "defaultToken": "@default", "defaultValue": "Patient", "defaultValueType": "String", "instance": null },
                  { "column": "WrittenOnUtc", "mode": "default", "defaultToken": "@now", "defaultValueType": "DateTime", "instance": null }
                ] }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task Default_value_columns_persist_the_at_token_as_JsonPath_and_the_literal_as_DefaultValue()
    {
        var (service, repository, _, destinationId, sourceConnectionId) = CreateSut(existingDestinationTables: ["Patient"]);

        var body = DefaultValueColumnFixture(sourceConnectionId, destinationId);
        var result = await service.ImportAsync(Parse(body), CancellationToken.None);

        result.Profiles.Single().Warnings.Should().BeEmpty();

        var profiles = await repository.GetMappingProfilesAsync(CancellationToken.None);
        var fields = profiles.Single().Fields;

        var literal = fields.Single(f => f.TargetField == "ClientType");
        literal.JsonPath.Should().Be("@default");
        literal.DefaultValue.Should().Be("Patient");

        var token = fields.Single(f => f.TargetField == "WrittenOnUtc");
        token.JsonPath.Should().Be("@now");
        // Only the literal "@default" case carries a DefaultValue — a runtime token's value comes from
        // JsonMappingEngine's systemValues dictionary at pipeline-run time, never from this column.
        token.DefaultValue.Should().BeNull();
    }

    [Fact]
    public async Task Import_throws_when_mappings_is_missing()
    {
        var (service, _, _, destinationId, sourceConnectionId) = CreateSut(existingDestinationTables: []);
        var body = $$"""
            { "source": "EPIC", "destination": "SQL", "sourceConnectionId": "{{sourceConnectionId}}", "destinationId": "{{destinationId}}" }
            """;

        var act = () => service.ImportAsync(Parse(body), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static string RenamedRootTableFixture(Guid sourceConnectionId, Guid destinationId) => $$"""
        {
          "source": "EPIC", "destination": "SQL",
          "sourceConnectionId": "{{sourceConnectionId}}", "destinationId": "{{destinationId}}",
          "mappings": [
            {
              "resourceType": "Patient", "rank": 1, "generatedAt": "2026-07-21T16:10:52.564Z",
              "schemaChanges": { "tablesToCreate": [], "columnsToAdd": [], "summary": null },
              "processingOrder": [
                { "step": 1, "table": "PatientV2", "level": 1, "dependsOn": null, "note": null }
              ],
              "destination": "SQL",
              "tables": [
                { "name": "PatientV2", "isNew": false, "relation": null, "columns": [
                  { "column": "Id", "mode": "directField", "sources": ["Patient.id"], "instance": null }
                ] }
              ]
            }
          ]
        }
        """;

    private static string RelationFallbackFixture(Guid sourceConnectionId, Guid destinationId) => $$"""
        {
          "source": "EPIC", "destination": "SQL",
          "sourceConnectionId": "{{sourceConnectionId}}", "destinationId": "{{destinationId}}",
          "mappings": [
            {
              "resourceType": "D", "rank": 1, "generatedAt": "2026-07-21T16:10:52.564Z",
              "schemaChanges": { "tablesToCreate": [], "columnsToAdd": [], "summary": null },
              "processingOrder": [
                { "step": 1, "table": "D", "level": 1, "dependsOn": null, "note": null },
                { "step": 2, "table": "DChild", "level": 2, "dependsOn": null, "note": null }
              ],
              "destination": "SQL",
              "tables": [
                { "name": "D", "isNew": false, "relation": null, "columns": [
                  { "column": "Id", "mode": "directField", "sources": ["D.id"], "instance": null }
                ] },
                { "name": "DChild", "isNew": false, "relation": null, "columns": [
                  {
                    "column": "ParentId", "mode": "directField", "sources": ["D.child.parentId"],
                    "instance": { "arrayContext": "D.child", "type": "all", "aggregate": "rows" }
                  }
                ] }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// Regression test for a real production bug: a Condition.code column's stored arrayContext
    /// ("Condition.code.coding") naively wildcards EVERY segment it spans ("code" AND "coding"), producing
    /// "$.code[*].coding[*].code" — but Condition.code is a single 0..1 CodeableConcept, not itself repeating;
    /// only "coding" is. That malformed path resolves to zero matches in JsonMappingEngine.ResolveAll, so the
    /// column silently mapped to null for every resource. The fix: when the FHIR element catalog has a real,
    /// known entry for this exact fhirPath, use its own pre-computed (and correct) JsonPath directly instead
    /// of re-deriving one from arrayContext's segment count.
    /// </summary>
    [Fact]
    public async Task Import_prefers_the_catalogs_own_JsonPath_over_the_arrayContext_heuristic_when_a_real_element_matches()
    {
        var catalog = new FakeFhirElementCatalog(new Dictionary<string, IReadOnlyList<FhirElementDto>>
        {
            ["Condition"] = new List<FhirElementDto>
            {
                new("Code › Coding › Code", "$.code.coding[*].code", "code.coding.code", "0..*", "String", true, ["code.coding"], []),
            },
        });
        var (service, repository, _, destinationId, sourceConnectionId) = CreateSut(existingDestinationTables: ["Condition"], catalog);

        const string body = """
            {
              "source": "EPIC",
              "destination": "SQL",
              "sourceConnectionId": "SOURCE_CONNECTION_ID",
              "destinationId": "DESTINATION_ID",
              "mappings": [
                {
                  "resourceType": "Condition",
                  "rank": 1,
                  "generatedAt": "2026-07-21T16:10:52.564Z",
                  "schemaChanges": { "tablesToCreate": [], "columnsToAdd": [], "summary": "no schema changes" },
                  "processingOrder": [ { "step": 1, "table": "Condition", "level": 1, "dependsOn": null, "note": null } ],
                  "destination": { "type": "mssql", "label": "MSSQL Server" },
                  "tables": [
                    {
                      "name": "Condition",
                      "isNew": false,
                      "relation": null,
                      "columns": [
                        {
                          "column": "Code",
                          "mode": "directField",
                          "sources": ["Condition.code.coding.code"],
                          "instance": { "arrayContext": "Condition.code.coding", "type": "first", "aggregate": "rows" }
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;
        var requestJson = body
            .Replace("SOURCE_CONNECTION_ID", sourceConnectionId.ToString())
            .Replace("DESTINATION_ID", destinationId.ToString());

        await service.ImportAsync(Parse(requestJson), CancellationToken.None);

        var profiles = await repository.GetMappingProfilesAsync(CancellationToken.None);
        var profile = profiles.Single();
        var codeField = profile.Fields.Single(f => f.TargetField == "Code");
        codeField.JsonPath.Should().Be("$.code.coding[*].code",
            "the catalog's own known-correct JsonPath must win over the buggy arrayContext-derived guess");
    }

    private static string FullPatientFixture(Guid sourceConnectionId, Guid destinationId) => $$"""
        {
          "source": "EPIC",
          "destination": "SQL",
          "sourceConnectionId": "{{sourceConnectionId}}",
          "destinationId": "{{destinationId}}",
          "mappings": [
            {
              "resourceType": "Patient",
              "rank": 1,
              "generatedAt": "2026-07-21T16:10:52.564Z",
              "schemaChanges": {
                "tablesToCreate": [
                  {
                    "name": "PatientName",
                    "relation": { "childColumn": "PatientId", "parentTable": "Patient", "parentColumn": "Id" },
                    "columns": [
                      { "name": "Id", "dataType": "bigint", "isPrimaryKey": true, "isForeignKey": false, "references": null },
                      { "name": "PatientId", "dataType": "bigint", "isPrimaryKey": false, "isForeignKey": true, "references": "Patient.Id" },
                      { "name": "use", "dataType": "nvarchar(120)", "isPrimaryKey": false, "isForeignKey": false, "references": null },
                      { "name": "text", "dataType": "nvarchar(120)", "isPrimaryKey": false, "isForeignKey": false, "references": null },
                      { "name": "family", "dataType": "nvarchar(120)", "isPrimaryKey": false, "isForeignKey": false, "references": null },
                      { "name": "given", "dataType": "nvarchar(120)", "isPrimaryKey": false, "isForeignKey": false, "references": null }
                    ]
                  },
                  {
                    "name": "PatientTelecom",
                    "relation": { "childColumn": "PatientId", "parentTable": "Patient", "parentColumn": "Id" },
                    "columns": [
                      { "name": "Id", "dataType": "bigint", "isPrimaryKey": true, "isForeignKey": false, "references": null },
                      { "name": "PatientId", "dataType": "bigint", "isPrimaryKey": false, "isForeignKey": true, "references": "Patient.Id" },
                      { "name": "system", "dataType": "nvarchar(120)", "isPrimaryKey": false, "isForeignKey": false, "references": null },
                      { "name": "value", "dataType": "nvarchar(120)", "isPrimaryKey": false, "isForeignKey": false, "references": null }
                    ]
                  },
                  {
                    "name": "PatientAddres",
                    "relation": { "childColumn": "PatientId", "parentTable": "Patient", "parentColumn": "Id" },
                    "columns": [
                      { "name": "Id", "dataType": "bigint", "isPrimaryKey": true, "isForeignKey": false, "references": null },
                      { "name": "PatientId", "dataType": "bigint", "isPrimaryKey": false, "isForeignKey": true, "references": "Patient.Id" },
                      { "name": "use", "dataType": "nvarchar(120)", "isPrimaryKey": false, "isForeignKey": false, "references": null },
                      { "name": "line", "dataType": "nvarchar(120)", "isPrimaryKey": false, "isForeignKey": false, "references": null },
                      { "name": "city", "dataType": "nvarchar(120)", "isPrimaryKey": false, "isForeignKey": false, "references": null },
                      { "name": "state", "dataType": "nvarchar(120)", "isPrimaryKey": false, "isForeignKey": false, "references": null },
                      { "name": "postalCode", "dataType": "nvarchar(120)", "isPrimaryKey": false, "isForeignKey": false, "references": null }
                    ]
                  },
                  {
                    "name": "PatientPractitioner",
                    "relation": { "childColumn": "PatientId", "parentTable": "Patient", "parentColumn": "Id" },
                    "columns": [
                      { "name": "Id", "dataType": "bigint", "isPrimaryKey": true, "isForeignKey": false, "references": null },
                      { "name": "PatientId", "dataType": "bigint", "isPrimaryKey": false, "isForeignKey": true, "references": "Patient.Id" },
                      { "name": "reference", "dataType": "nvarchar(120)", "isPrimaryKey": false, "isForeignKey": false, "references": null }
                    ]
                  },
                  {
                    "name": "PatientContactRelation",
                    "relation": null,
                    "columns": [
                      { "name": "Id", "dataType": "bigint", "isPrimaryKey": true, "isForeignKey": false, "references": null },
                      { "name": "Relation", "dataType": "nvarchar(500)", "isPrimaryKey": false, "isForeignKey": false, "references": null }
                    ]
                  },
                  {
                    "name": "PatientAddCombo",
                    "relation": null,
                    "columns": [
                      { "name": "Id", "dataType": "bigint", "isPrimaryKey": true, "isForeignKey": false, "references": null },
                      { "name": "AddressCombo", "dataType": "nvarchar(500)", "isPrimaryKey": false, "isForeignKey": false, "references": null }
                    ]
                  }
                ],
                "columnsToAdd": [
                  { "table": "Patient", "name": "DeceasedBoolean", "dataType": "Bit" },
                  { "table": "Patient", "name": "Active", "dataType": "bit" }
                ],
                "summary": "6 new tables to create, 2 new columns on existing tables"
              },
              "processingOrder": [
                { "step": 1, "table": "Patient", "level": 1, "dependsOn": null, "note": null },
                { "step": 2, "table": "PatientAddCombo", "level": 1, "dependsOn": null, "note": null },
                { "step": 3, "table": "PatientContactRelation", "level": 1, "dependsOn": null, "note": null },
                { "step": 4, "table": "PatientAddres", "level": 2, "dependsOn": "Patient", "note": null },
                { "step": 5, "table": "PatientName", "level": 2, "dependsOn": "Patient", "note": null },
                { "step": 6, "table": "PatientPractitioner", "level": 2, "dependsOn": "Patient", "note": null },
                { "step": 7, "table": "PatientTelecom", "level": 2, "dependsOn": "Patient", "note": null }
              ],
              "destination": { "type": "mssql", "label": "MSSQL Server" },
              "tables": [
                {
                  "name": "Patient",
                  "isNew": false,
                  "relation": null,
                  "columns": [
                    { "column": "Id", "mode": "directField", "sources": ["Patient.id"], "instance": null },
                    { "column": "Active", "mode": "directField", "sources": ["Patient.active"], "instance": null },
                    { "column": "DeceasedBoolean", "mode": "directField", "sources": ["Patient.deceasedBoolean"], "instance": null }
                  ]
                },
                {
                  "name": "PatientName",
                  "isNew": true,
                  "relation": { "childColumn": "PatientId", "parentTable": "Patient", "parentColumn": "Id" },
                  "columns": [
                    {
                      "column": "use", "mode": "directField", "sources": ["Patient.name.use"],
                      "instance": { "arrayContext": "Patient.name", "type": "all", "aggregate": "rows" }
                    },
                    {
                      "column": "text", "mode": "directField", "sources": ["Patient.name.text"],
                      "instance": { "arrayContext": "Patient.name", "type": "all", "aggregate": "rows" }
                    },
                    {
                      "column": "family", "mode": "directField", "sources": ["Patient.name.family"],
                      "instance": { "arrayContext": "Patient.name", "type": "all", "aggregate": "rows" }
                    },
                    {
                      "column": "given", "mode": "directField", "sources": ["Patient.name.given"],
                      "instance": { "arrayContext": "Patient.name", "type": "all", "aggregate": "csv" }
                    }
                  ]
                },
                {
                  "name": "PatientTelecom",
                  "isNew": true,
                  "relation": { "childColumn": "PatientId", "parentTable": "Patient", "parentColumn": "Id" },
                  "columns": [
                    {
                      "column": "system", "mode": "directField", "sources": ["Patient.telecom.system"],
                      "instance": { "arrayContext": "Patient.telecom", "type": "all", "aggregate": "rows" }
                    },
                    {
                      "column": "value", "mode": "directField", "sources": ["Patient.telecom.value"],
                      "instance": { "arrayContext": "Patient.telecom", "type": "all", "aggregate": "rows" }
                    },
                    {
                      "column": "use", "mode": "directField", "sources": ["Patient.telecom.use"],
                      "instance": { "arrayContext": "Patient.telecom", "type": "all", "aggregate": "rows" }
                    },
                    {
                      "column": "rank", "mode": "directField", "sources": ["Patient.telecom.rank"],
                      "instance": { "arrayContext": "Patient.telecom", "type": "all", "aggregate": "rows" }
                    }
                  ]
                },
                {
                  "name": "PatientAddres",
                  "isNew": true,
                  "relation": { "childColumn": "PatientId", "parentTable": "Patient", "parentColumn": "Id" },
                  "columns": [
                    {
                      "column": "use", "mode": "directField", "sources": ["Patient.address.use"],
                      "instance": { "arrayContext": "Patient.address", "type": "all", "aggregate": "rows" }
                    },
                    {
                      "column": "line", "mode": "directField", "sources": ["Patient.address.line"],
                      "instance": { "arrayContext": "Patient.address", "type": "all", "aggregate": "rows" }
                    },
                    {
                      "column": "city", "mode": "directField", "sources": ["Patient.address.city"],
                      "instance": { "arrayContext": "Patient.address", "type": "all", "aggregate": "rows" }
                    },
                    {
                      "column": "state", "mode": "directField", "sources": ["Patient.address.state"],
                      "instance": { "arrayContext": "Patient.address", "type": "all", "aggregate": "rows" }
                    },
                    {
                      "column": "postalCode", "mode": "directField", "sources": ["Patient.address.postalCode"],
                      "instance": { "arrayContext": "Patient.address", "type": "all", "aggregate": "rows" }
                    },
                    {
                      "column": "district", "mode": "directField", "sources": ["Patient.address.district"],
                      "instance": { "arrayContext": "Patient.address", "type": "all", "aggregate": "rows" }
                    },
                    {
                      "column": "periodStart", "mode": "directField", "sources": ["Patient.address.period.start"],
                      "instance": { "arrayContext": "Patient.address", "type": "all", "aggregate": "rows" }
                    }
                  ]
                },
                {
                  "name": "PatientPractitioner",
                  "isNew": true,
                  "relation": { "childColumn": "PatientId", "parentTable": "Patient", "parentColumn": "Id" },
                  "columns": [
                    {
                      "column": "reference", "mode": "directField", "sources": ["Patient.generalPractitioner.reference"],
                      "instance": { "arrayContext": "Patient.generalPractitioner", "type": "all", "aggregate": "rows" }
                    },
                    {
                      "column": "type", "mode": "directField", "sources": ["Patient.generalPractitioner.type"],
                      "instance": { "arrayContext": "Patient.generalPractitioner", "type": "all", "aggregate": "rows" }
                    },
                    {
                      "column": "display", "mode": "directField", "sources": ["Patient.generalPractitioner.display"],
                      "instance": { "arrayContext": "Patient.generalPractitioner", "type": "all", "aggregate": "rows" }
                    }
                  ]
                },
                {
                  "name": "PatientContactRelation",
                  "isNew": true,
                  "relation": null,
                  "columns": [
                    {
                      "column": "Relation", "mode": "wholeNodeAsJson", "sourceNode": "Patient.contact.relationship",
                      "instance": { "arrayContext": "Patient.contact.relationship", "type": "all", "aggregate": "rows" }
                    }
                  ]
                },
                {
                  "name": "PatientAddCombo",
                  "isNew": true,
                  "relation": null,
                  "columns": [
                    {
                      "column": "AddressCombo", "mode": "joinedFields",
                      "sources": [
                        "Patient.contact.address.city", "Patient.contact.address.district",
                        "Patient.contact.address.state", "Patient.contact.address.postalCode"
                      ],
                      "delimiter": ",",
                      "instance": { "arrayContext": "Patient.contact", "type": "all", "aggregate": "rows" }
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static string RankOrderingFixture(Guid sourceConnectionId, Guid destinationId) => $$"""
        {
          "source": "EPIC", "destination": "SQL",
          "sourceConnectionId": "{{sourceConnectionId}}", "destinationId": "{{destinationId}}",
          "mappings": [
            {
              "resourceType": "B", "rank": 2, "generatedAt": "2026-07-21T16:10:52.564Z",
              "schemaChanges": {
                "tablesToCreate": [
                  {
                    "name": "BChild",
                    "relation": { "childColumn": "LookupId", "parentTable": "SharedLookup", "parentColumn": "Id" },
                    "columns": [
                      { "name": "Id", "dataType": "bigint", "isPrimaryKey": true, "isForeignKey": false, "references": null },
                      { "name": "LookupId", "dataType": "bigint", "isPrimaryKey": false, "isForeignKey": true, "references": "SharedLookup.Id" }
                    ]
                  }
                ],
                "columnsToAdd": [], "summary": null
              },
              "processingOrder": [
                { "step": 1, "table": "B", "level": 1, "dependsOn": null, "note": null },
                { "step": 2, "table": "BChild", "level": 2, "dependsOn": "SharedLookup", "note": null }
              ],
              "destination": "SQL",
              "tables": [
                { "name": "B", "isNew": false, "relation": null, "columns": [
                  { "column": "Id", "mode": "directField", "sources": ["B.id"], "instance": null }
                ] },
                { "name": "BChild", "isNew": true, "relation": { "childColumn": "LookupId", "parentTable": "SharedLookup", "parentColumn": "Id" }, "columns": [
                  { "column": "LookupId", "mode": "directField", "sources": ["B.lookup.id"], "instance": null }
                ] }
              ]
            },
            {
              "resourceType": "A", "rank": 1, "generatedAt": "2026-07-21T16:10:52.564Z",
              "schemaChanges": {
                "tablesToCreate": [
                  {
                    "name": "SharedLookup", "relation": null,
                    "columns": [
                      { "name": "Id", "dataType": "bigint", "isPrimaryKey": true, "isForeignKey": false, "references": null }
                    ]
                  }
                ],
                "columnsToAdd": [], "summary": null
              },
              "processingOrder": [
                { "step": 1, "table": "A", "level": 1, "dependsOn": null, "note": null },
                { "step": 2, "table": "SharedLookup", "level": 1, "dependsOn": null, "note": null }
              ],
              "destination": "SQL",
              "tables": [
                { "name": "A", "isNew": false, "relation": null, "columns": [
                  { "column": "Id", "mode": "directField", "sources": ["A.id"], "instance": null }
                ] }
              ]
            }
          ]
        }
        """;

    private static string StepOrderingFixture(Guid sourceConnectionId, Guid destinationId) => $$"""
        {
          "source": "EPIC", "destination": "SQL",
          "sourceConnectionId": "{{sourceConnectionId}}", "destinationId": "{{destinationId}}",
          "mappings": [
            {
              "resourceType": "C", "rank": 1, "generatedAt": "2026-07-21T16:10:52.564Z",
              "schemaChanges": {
                "tablesToCreate": [
                  {
                    "name": "CChild",
                    "relation": { "childColumn": "ParentId", "parentTable": "CParent", "parentColumn": "Id" },
                    "columns": [
                      { "name": "Id", "dataType": "bigint", "isPrimaryKey": true, "isForeignKey": false, "references": null },
                      { "name": "ParentId", "dataType": "bigint", "isPrimaryKey": false, "isForeignKey": true, "references": "CParent.Id" }
                    ]
                  },
                  {
                    "name": "CParent", "relation": null,
                    "columns": [
                      { "name": "Id", "dataType": "bigint", "isPrimaryKey": true, "isForeignKey": false, "references": null }
                    ]
                  }
                ],
                "columnsToAdd": [], "summary": null
              },
              "processingOrder": [
                { "step": 1, "table": "C", "level": 1, "dependsOn": null, "note": null },
                { "step": 2, "table": "CParent", "level": 1, "dependsOn": null, "note": null },
                { "step": 3, "table": "CChild", "level": 2, "dependsOn": "CParent", "note": null }
              ],
              "destination": "SQL",
              "tables": [
                { "name": "C", "isNew": false, "relation": null, "columns": [
                  { "column": "Id", "mode": "directField", "sources": ["C.id"], "instance": null }
                ] },
                { "name": "CParent", "isNew": true, "relation": null, "columns": [
                  { "column": "Id", "mode": "directField", "sources": ["C.parent.id"], "instance": null }
                ] },
                { "name": "CChild", "isNew": true, "relation": { "childColumn": "ParentId", "parentTable": "CParent", "parentColumn": "Id" }, "columns": [
                  { "column": "ParentId", "mode": "directField", "sources": ["C.child.id"], "instance": { "arrayContext": "C.child", "type": "all", "aggregate": "rows" } }
                ] }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// In-memory fake for <see cref="IMappingSchemaProvider"/> — records call order and lets tests seed
    /// pre-existing tables/columns/live-column-types, without a real database. Each <see cref="BeginTransactionAsync"/>
    /// call hands out a fresh <see cref="RecordingSchemaTransaction"/> whose creates/adds are only merged into
    /// this provider's permanent (i.e. "committed") state on <see cref="IMappingSchemaTransaction.CommitAsync"/> —
    /// disposing without committing discards them, mirroring a real SQL transaction rollback.
    /// </summary>
    private sealed class RecordingSchemaProvider : IMappingSchemaProvider
    {
        public List<string> CreatedTables { get; } = [];
        public List<string> AddedColumns { get; } = [];
        public Dictionary<string, IReadOnlySet<string>> ExplicitlyMappedColumnsByTable { get; } = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _existingTables;
        private readonly HashSet<(string Table, string Column)> _existingColumns = new();

        public Dictionary<(string Table, string Column), string> LiveColumnTypes { get; } = new();
        public Dictionary<string, TableRelationDto> LiveForeignKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Table names that should throw when <c>CreateTableAsync</c> is called for them, to simulate
        /// a DDL failure partway through a resourceType's schema changes.</summary>
        public HashSet<string> FailOnCreateTable { get; } = new(StringComparer.OrdinalIgnoreCase);

        public RecordingSchemaProvider(IEnumerable<string>? existingTables = null)
        {
            _existingTables = new HashSet<string>(existingTables ?? [], StringComparer.OrdinalIgnoreCase);
        }

        public Task<IMappingSchemaTransaction> BeginTransactionAsync(
            DestinationConfiguration destination, CancellationToken cancellationToken) =>
            Task.FromResult<IMappingSchemaTransaction>(new RecordingSchemaTransaction(this));

        private sealed class RecordingSchemaTransaction : IMappingSchemaTransaction
        {
            private readonly RecordingSchemaProvider _owner;
            private readonly List<string> _pendingTables = [];
            private readonly List<(string Table, string Column)> _pendingColumns = [];

            public RecordingSchemaTransaction(RecordingSchemaProvider owner) => _owner = owner;

            public Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken) =>
                Task.FromResult(_owner._existingTables.Contains(tableName) || _pendingTables.Contains(tableName));

            public Task<bool> ColumnExistsAsync(string tableName, string columnName, CancellationToken cancellationToken) =>
                Task.FromResult(
                    _owner._existingColumns.Contains((tableName, columnName))
                    || _pendingColumns.Contains((tableName, columnName)));

            public Task CreateTableAsync(
                TableDefinitionDto table, IReadOnlySet<string> explicitlyMappedColumns, CancellationToken cancellationToken)
            {
                if (_owner.FailOnCreateTable.Contains(table.Name))
                {
                    throw new InvalidOperationException($"Simulated DDL failure creating '{table.Name}'.");
                }

                _owner.ExplicitlyMappedColumnsByTable[table.Name] = explicitlyMappedColumns;
                _pendingTables.Add(table.Name);
                return Task.CompletedTask;
            }

            public Task AddColumnAsync(string tableName, ColumnToAddDto column, CancellationToken cancellationToken)
            {
                _pendingColumns.Add((tableName, column.Name));
                return Task.CompletedTask;
            }

            public Task<string?> GetColumnDataTypeAsync(string tableName, string columnName, CancellationToken cancellationToken)
            {
                _owner.LiveColumnTypes.TryGetValue((tableName, columnName), out var type);
                return Task.FromResult(type);
            }

            public Task<TableRelationDto?> GetForeignKeyAsync(string tableName, CancellationToken cancellationToken)
            {
                _owner.LiveForeignKeys.TryGetValue(tableName, out var relation);
                return Task.FromResult(relation);
            }

            public Task CommitAsync(CancellationToken cancellationToken)
            {
                foreach (var table in _pendingTables)
                {
                    _owner._existingTables.Add(table);
                    _owner.CreatedTables.Add(table);
                }

                foreach (var (table, column) in _pendingColumns)
                {
                    _owner._existingColumns.Add((table, column));
                    _owner.AddedColumns.Add($"{table}.{column}");
                }

                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() =>
                // Committed state was already merged into the owner above; anything still only in the pending
                // lists here (i.e. never committed) is simply discarded — the fake's equivalent of a real
                // SqlTransaction rollback.
                ValueTask.CompletedTask;
        }
    }
}
