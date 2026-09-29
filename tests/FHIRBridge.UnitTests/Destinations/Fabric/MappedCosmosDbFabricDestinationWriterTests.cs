using System.Text.Json.Nodes;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Destinations.Fabric;

/// <summary>
/// Covers document construction and key resolution — the decisions made before anything reaches Cosmos. The write
/// itself needs a live container, so it is not faked here: a mocked CosmosClient would only assert this test's own
/// assumptions about the SDK.
/// </summary>
public sealed class MappedCosmosDbFabricDestinationWriterTests
{
    private static MappedDestinationRecord Record(Dictionary<string, object?> values) =>
        new(Guid.NewGuid(), "Patient", "Patients", "src-1", values);

    private static MappingProfile Mapping(params MappingField[] fields) =>
        new("Cosmos Mapping", "Patient", Guid.NewGuid(), Guid.NewGuid(), "Patients", fields);

    /// <summary>Only the members these tests care about; everything else takes MappingField's own defaults.</summary>
    private static MappingField Field(
        string targetField,
        bool isUpsertKey = false,
        bool isEnabled = true,
        string? destinationObject = null) =>
        new(
            TargetField: targetField,
            JsonPath: "$.id",
            ValueType: MappingValueType.String,
            IsRequired: false,
            DefaultValue: null,
            Format: null,
            DestinationObject: destinationObject,
            IsEnabled: isEnabled,
            IsUpsertKey: isUpsertKey);

    /// <summary>
    /// Cosmos identifies a document by <c>id</c> within its partition and rejects a document without one, so the
    /// writer must always supply it — this is the field with no safe default.
    /// </summary>
    [Fact]
    public void Every_document_carries_an_id()
    {
        var document = MappedCosmosDbFabricDestinationWriter.ToJsonDocument(
            Record(new() { ["GivenName"] = "Franklin" }), keyField: null);

        document["id"].Should().NotBeNull();
        document["id"].Should().BeOfType<string>().Which.Should().NotBeEmpty();
    }

    /// <summary>
    /// The mapped key becomes the document id, which is what makes a re-run replace a document rather than add a
    /// second copy of it.
    /// </summary>
    [Fact]
    public void The_mapped_key_value_becomes_the_document_id()
    {
        var document = MappedCosmosDbFabricDestinationWriter.ToJsonDocument(
            Record(new() { ["SourceResourceId"] = "patient-42", ["GivenName"] = "Franklin" }),
            keyField: "SourceResourceId");

        document["id"].Should().Be("patient-42");
    }

    /// <summary>
    /// A mapping that targets "id" directly has said explicitly what identifies the document, so it wins over the
    /// derived id — otherwise the writer would overwrite a value the customer deliberately mapped.
    /// </summary>
    [Fact]
    public void An_explicitly_mapped_id_wins_over_the_derived_one()
    {
        var document = MappedCosmosDbFabricDestinationWriter.ToJsonDocument(
            Record(new() { ["id"] = "chosen-by-mapping", ["SourceResourceId"] = "patient-42" }),
            keyField: "SourceResourceId");

        document["id"].Should().Be("chosen-by-mapping");
    }

    /// <summary>
    /// With nothing identifying the record, a generated id is used rather than failing the write: Cosmos rejects
    /// an id-less document outright, and an append-only feed is a legitimate configuration. Two records must not
    /// collide on that generated value.
    /// </summary>
    [Fact]
    public void Records_with_no_key_get_distinct_generated_ids()
    {
        var first = MappedCosmosDbFabricDestinationWriter.ToJsonDocument(
            Record(new() { ["GivenName"] = "Franklin" }), keyField: null);
        var second = MappedCosmosDbFabricDestinationWriter.ToJsonDocument(
            Record(new() { ["GivenName"] = "Franklin" }), keyField: null);

        first["id"].Should().NotBe(second["id"]);
    }

    /// <summary>
    /// A key field that is mapped but null for this particular record falls back to a generated id rather than
    /// writing a document with an empty id, which Cosmos would reject.
    /// </summary>
    [Fact]
    public void A_null_key_value_falls_back_to_a_generated_id()
    {
        var document = MappedCosmosDbFabricDestinationWriter.ToJsonDocument(
            Record(new() { ["SourceResourceId"] = null, ["GivenName"] = "Franklin" }),
            keyField: "SourceResourceId");

        document["id"].Should().BeOfType<string>().Which.Should().NotBeEmpty();
    }

    /// <summary>
    /// Every mapped value reaches the document. Values are written as strings because the mapping pipeline hands
    /// every cell over already stringly-typed — emitting a number would mean asserting a type the pipeline never
    /// established.
    /// </summary>
    [Fact]
    public void Mapped_values_are_written_as_strings()
    {
        var document = MappedCosmosDbFabricDestinationWriter.ToJsonDocument(
            Record(new() { ["GivenName"] = "Franklin", ["Age"] = 42 }), keyField: null);

        document["GivenName"].Should().Be("Franklin");
        document["Age"].Should().Be("42");
    }

    /// <summary>
    /// A null mapped value stays null rather than becoming the string "" — here the pipeline HAS said something
    /// (this field has no value), so the document says the same.
    /// </summary>
    [Fact]
    public void A_null_mapped_value_stays_null()
    {
        var document = MappedCosmosDbFabricDestinationWriter.ToJsonDocument(
            Record(new() { ["MiddleName"] = null, ["GivenName"] = "Franklin" }), keyField: null);

        document["MiddleName"].Should().BeNull();
    }

    /// <summary>
    /// The upsert key follows the same convention as the SQL Server, Mongo and Warehouse writers — the flagged
    /// field, else SourceResourceId — so a key configured once means the same thing on every destination.
    /// </summary>
    [Fact]
    public void The_flagged_mapping_field_is_the_upsert_key()
    {
        var mapping = Mapping(
            Field("MedicalRecordNumber", isUpsertKey: true));

        MappedCosmosDbFabricDestinationWriter.ResolveUpsertKeyField(mapping)
            .Should().Be("MedicalRecordNumber");
    }

    [Fact]
    public void SourceResourceId_is_the_fallback_key()
    {
        var mapping = Mapping(Field("SourceResourceId"));

        MappedCosmosDbFabricDestinationWriter.ResolveUpsertKeyField(mapping).Should().Be("SourceResourceId");
    }

    /// <summary>
    /// With neither a flagged key nor SourceResourceId mapped there is no key at all, and the writer generates
    /// ids instead. Null rather than an exception: an append-only feed is a legitimate setup, and the writer logs
    /// which of the two happened.
    /// </summary>
    [Fact]
    public void No_key_at_all_resolves_to_null()
    {
        var mapping = Mapping(Field("GivenName"));

        MappedCosmosDbFabricDestinationWriter.ResolveUpsertKeyField(mapping).Should().BeNull();
    }

    /// <summary>
    /// A disabled field is not a key. The canvas keeps disabled fields in the profile, so an unscoped search
    /// would resurrect a key the user switched off.
    /// </summary>
    [Fact]
    public void A_disabled_field_is_not_the_key()
    {
        var mapping = Mapping(
            Field("MedicalRecordNumber", isUpsertKey: true, isEnabled: false));

        MappedCosmosDbFabricDestinationWriter.ResolveUpsertKeyField(mapping).Should().BeNull();
    }

    /// <summary>
    /// A key flagged for a DIFFERENT destination object belongs to another container, so it must not be picked
    /// up here — the per-object scoping every other writer applies.
    /// </summary>
    [Fact]
    public void A_key_scoped_to_another_destination_object_is_ignored()
    {
        var mapping = Mapping(
            Field("ContactId", isUpsertKey: true, destinationObject: "PatientContacts"));

        MappedCosmosDbFabricDestinationWriter.ResolveUpsertKeyField(mapping).Should().BeNull();
    }

    /// <summary>
    /// The document must contain no System.Text.Json node types at all.
    ///
    /// <para>The Cosmos SDK serializes items with NEWTONSOFT, which walks a JsonObject as an IDictionary and
    /// follows every child's Parent back to the root — "Self referencing loop detected for property 'Parent'".
    /// It only surfaces against a real container, so this asserts the property that prevents it rather than the
    /// symptom: every value is a plain CLR type both serializers agree on.</para>
    /// </summary>
    [Fact]
    public void A_document_carries_no_system_text_json_nodes()
    {
        var address = JsonNode.Parse("""[{"city":"Boston","line":["1 Main St"]}]""");

        var document = MappedCosmosDbFabricDestinationWriter.ToJsonDocument(
            Record(new Dictionary<string, object?> { ["Address"] = address }), keyField: null);

        document.Values.Should().NotContain(value => value is JsonNode);
    }

    /// <summary>
    /// A field routed through a Transformation node arrives as a LIVE JsonNode rather than as the raw JSON text
    /// an ordinary StoreJson field carries. Both must land as the same JSON text, so a transform in the graph
    /// cannot change what is stored.
    /// </summary>
    [Fact]
    public void A_live_json_node_is_stored_as_its_json_text()
    {
        const string json = """{"city":"Boston"}""";

        var fromTransform = MappedCosmosDbFabricDestinationWriter.ToJsonDocument(
            Record(new Dictionary<string, object?> { ["Address"] = JsonNode.Parse(json) }), keyField: null);

        var fromMapping = MappedCosmosDbFabricDestinationWriter.ToJsonDocument(
            Record(new Dictionary<string, object?> { ["Address"] = json }), keyField: null);

        fromTransform["Address"].Should().Be(json);
        fromTransform["Address"].Should().Be(fromMapping["Address"]);
    }

    /// <summary>
    /// A JsonValue holding a string stores the bare value, not a quoted one. ToJsonString() would write
    /// "\"Smith\"" into the document — visible only once someone read the data back.
    /// </summary>
    [Fact]
    public void A_scalar_json_value_is_stored_unquoted()
    {
        var document = MappedCosmosDbFabricDestinationWriter.ToJsonDocument(
            Record(new Dictionary<string, object?> { ["LastName"] = JsonValue.Create("Smith") }),
            keyField: null);

        document["LastName"].Should().Be("Smith");
    }
}
