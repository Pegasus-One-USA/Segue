using System.Text.Json;
using FHIRBridge.Application.Services.Workflows;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;
using Node = FHIRBridge.Application.Services.Workflows.WorkflowResourceTypeSubsetRule.Node;
using Edge = FHIRBridge.Application.Services.Workflows.WorkflowResourceTypeSubsetRule.Edge;

namespace FHIRBridge.UnitTests.Workflows;

/// <summary>
/// The save-time rule that a destination only writes resource types its source reads: the source node declares the
/// types, destinations choose from them, and a legacy source without a declared list keeps today's behaviour.
/// </summary>
public sealed class WorkflowResourceTypeSubsetRuleTests
{
    private static string Fields(params (string Key, string Value)[] fields) =>
        JsonSerializer.Serialize(fields.ToDictionary(field => field.Key, field => field.Value));

    // A source node saved by the portal's pickers: its "Resources" carries the declared-types marker.
    private static Node EhrSource(string id, string? resources, string name = "Epic") =>
        new(id, "EpicSourceNode", WorkflowNodeCategory.Source, name,
            resources is null
                ? Fields(("sourceConnectionId", Guid.NewGuid().ToString()))
                : Fields((WorkflowNodeResourceTypes.SourceTypesDeclaredKey, "true"), ("Resources", resources)));

    private static Node Destination(string id, string? destResources, string name = "Warehouse", string? destMappings = null)
    {
        var fields = new List<(string, string)>();
        if (destResources is not null)
        {
            fields.Add(("dest_resources", destResources));
        }

        if (destMappings is not null)
        {
            fields.Add(("dest_mappings", destMappings));
        }

        return new Node(id, "SqlServerDestinationNode", WorkflowNodeCategory.Destination, name, Fields([.. fields]));
    }

    private static Node Mapping(string id) =>
        new(id, "MappingNode", WorkflowNodeCategory.Transform, "Mapping", "{}");

    [Fact]
    public void A_destination_writing_a_subset_of_its_ehr_source_passes()
    {
        var error = WorkflowResourceTypeSubsetRule.Check(
            [EhrSource("src", "Patient, Observation, Condition"), Mapping("map"), Destination("dst", "Patient,Observation")],
            [new Edge("src", "map"), new Edge("map", "dst")]);

        error.Should().BeNull();
    }

    [Fact]
    public void A_destination_writing_a_type_its_ehr_source_does_not_read_is_rejected_with_names_and_types()
    {
        var error = WorkflowResourceTypeSubsetRule.Check(
            [EhrSource("src", "Patient", name: "Epic prod"), Destination("dst", "Patient,Procedure", name: "EHR write")],
            [new Edge("src", "dst")]);

        error.Should().Be(
            "Destination 'EHR write' writes Procedure, which its source 'Epic prod' does not read. " +
            "Add it to the source's resource types or remove it from the destination.");
    }

    [Fact]
    public void Resource_types_compare_case_insensitively()
    {
        var error = WorkflowResourceTypeSubsetRule.Check(
            [EhrSource("src", "patient"), Destination("dst", "Patient")],
            [new Edge("src", "dst")]);

        error.Should().BeNull();
    }

    [Fact]
    public void An_ehr_source_with_only_its_hidden_retrieval_resource_type_is_legacy_and_skipped()
    {
        // A node saved before sources declared their types: the EHR form wrote its hidden retrieval list (a stale
        // cloned list here) but no 'Resources', and the destinations decided what was fetched. Re-saving it unchanged
        // must still be accepted.
        var source = new Node("src", "AthenahealthSourceNode", WorkflowNodeCategory.Source, "athena",
            Fields(("Retrieval resource type", "Patient,Encounter")));

        WorkflowResourceTypeSubsetRule.Check([source, Destination("dst", "Patient,Encounter,Observation")], [new Edge("src", "dst")])
            .Should().BeNull();
    }

    [Fact]
    public void Enveloped_node_configuration_is_read_from_its_config_object()
    {
        var source = new Node("src", "GenericFhirSourceNode", WorkflowNodeCategory.Source, "FHIR",
            """{"ref":{"masterId":"x"},"config":{"Resource types declared":"true","Resources":"Patient"}}""");
        var destination = new Node("dst", "CsvDestinationNode", WorkflowNodeCategory.Destination, "CSV",
            """{"config":{"dest_resources":"Observation"}}""");

        WorkflowResourceTypeSubsetRule.Check([source, destination], [new Edge("src", "dst")])
            .Should().Contain("writes Observation");
    }

    [Fact]
    public void A_tabular_source_declares_its_types_through_tab_streams()
    {
        var streams = JsonSerializer.Serialize(new object[]
        {
            new { resourceType = "Patient", query = "select 1", template = new { } },
            new { resourceType = "AllergyIntolerance", query = "select 1", template = new { } },
        });
        var source = new Node("src", "TabularSourceNode", WorkflowNodeCategory.Source, "CSV",
            Fields(("tab_kind", "sql"), ("tab_streams", streams)));

        WorkflowResourceTypeSubsetRule.Check([source, Destination("dst", "AllergyIntolerance")], [new Edge("src", "dst")])
            .Should().BeNull();
        WorkflowResourceTypeSubsetRule.Check([source, Destination("dst", "Procedure")], [new Edge("src", "dst")])
            .Should().Contain("writes Procedure");
    }

    [Fact]
    public void A_legacy_tabular_source_declares_its_types_through_tab_templates()
    {
        var templates = JsonSerializer.Serialize(new object[]
        {
            new { template = new { resourceType = "Patient" } },
            new { resourceType = "Observation", template = new { resourceType = "Observation" } },
        });
        var source = new Node("src", "TabularSourceNode", WorkflowNodeCategory.Source, "CSV",
            Fields(("tab_kind", "csv"), ("tab_templates", templates)));

        WorkflowResourceTypeSubsetRule.Check([source, Destination("dst", "Patient,Observation")], [new Edge("src", "dst")])
            .Should().BeNull();
        WorkflowResourceTypeSubsetRule.Check([source, Destination("dst", "Condition")], [new Edge("src", "dst")])
            .Should().Contain("writes Condition");
    }

    [Fact]
    public void A_legacy_source_without_a_declared_list_skips_the_check()
    {
        var error = WorkflowResourceTypeSubsetRule.Check(
            [EhrSource("src", resources: null), Destination("dst", "Patient,Procedure")],
            [new Edge("src", "dst")]);

        error.Should().BeNull();
    }

    [Fact]
    public void Several_sources_feeding_one_destination_are_checked_against_their_union()
    {
        Node[] nodes =
        [
            EhrSource("a", "Patient", name: "Epic"),
            EhrSource("b", "Observation", name: "Cerner"),
            Destination("dst", "Patient,Observation"),
        ];
        Edge[] edges = [new Edge("a", "dst"), new Edge("b", "dst")];

        WorkflowResourceTypeSubsetRule.Check(nodes, edges).Should().BeNull();

        nodes[2] = Destination("dst", "Patient,Observation,Condition");
        WorkflowResourceTypeSubsetRule.Check(nodes, edges).Should()
            .Contain("writes Condition, which its sources 'Epic' and 'Cerner' do not read. Add it to the source's resource types");
    }

    [Fact]
    public void Only_the_upstream_source_counts_not_every_source_in_the_workflow()
    {
        var error = WorkflowResourceTypeSubsetRule.Check(
            [EhrSource("a", "Patient"), EhrSource("b", "Procedure"), Destination("dst", "Procedure")],
            [new Edge("a", "dst")]);

        error.Should().Contain("writes Procedure");
    }

    [Fact]
    public void A_destination_with_no_resource_types_and_an_analytics_node_are_ignored()
    {
        var analytics = new Node("ana", "PowerBiAnalyticsNode", WorkflowNodeCategory.Analytics, "BI",
            Fields(("dest_resources", "Procedure")));

        var error = WorkflowResourceTypeSubsetRule.Check(
            [EhrSource("src", "Patient"), Destination("dst", destResources: null), analytics],
            [new Edge("src", "dst"), new Edge("src", "ana")]);

        error.Should().BeNull();
    }

    [Fact]
    public void A_mapped_resource_counts_as_written()
    {
        var mappings = JsonSerializer.Serialize(new object[]
        {
            new { resource = "Patient", field = "id", column = "patient_id" },
            new { resource = "Encounter", field = "id", column = "encounter_id" },
        });

        var error = WorkflowResourceTypeSubsetRule.Check(
            [EhrSource("src", "Patient"), Destination("dst", "Patient", destMappings: mappings)],
            [new Edge("src", "dst")]);

        error.Should().Contain("writes Encounter");
    }

    [Fact]
    public void A_destination_with_no_upstream_source_is_not_checked()
    {
        var error = WorkflowResourceTypeSubsetRule.Check(
            [EhrSource("src", "Patient"), Destination("dst", "Procedure")],
            []);

        error.Should().BeNull();
    }

    // The 12 types the pre-step-B Generic FHIR form saved into "Resources" without the admin choosing them (the
    // portal's FHIR_RESOURCES).
    private const string GenericFhirSilentDefault =
        "Patient,Practitioner,Encounter,AllergyIntolerance,Observation,Condition,Procedure,ServiceRequest," +
        "DiagnosticReport,MedicationRequest,MedicationAdministration,Provenance";

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    public void ReadSourceDeclared_reads_Resources_when_the_node_carries_the_marker(string marker)
    {
        var configuration = Fields(
            (WorkflowNodeResourceTypes.SourceTypesDeclaredKey, marker), ("Resources", "Patient, Condition, patient"));

        WorkflowNodeResourceTypes.ReadSourceDeclared("EpicSourceNode", configuration)
            .Should().Equal("Patient", "Condition");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("yes")]
    public void ReadSourceDeclared_treats_an_unmarked_Resources_list_as_legacy(string? marker)
    {
        var fields = new List<(string, string)> { ("Resources", GenericFhirSilentDefault) };
        if (marker is not null)
        {
            fields.Add((WorkflowNodeResourceTypes.SourceTypesDeclaredKey, marker));
        }

        WorkflowNodeResourceTypes.ReadSourceDeclared("GenericFhirSourceNode", Fields([.. fields])).Should().BeNull();
    }

    [Fact]
    public void ReadSourceDeclared_reads_the_marker_through_the_settings_envelope_and_as_a_json_boolean()
    {
        WorkflowNodeResourceTypes.ReadSourceDeclared(
                "GenericFhirSourceNode",
                """{"ref":{"masterId":"x"},"config":{"Resource types declared":"true","Resources":"Patient"}}""")
            .Should().Equal("Patient");

        // The portal's graph mapper turns a stored boolean into the string "true" and reads it as declared, so the
        // server must too, or the two would disagree on whether the node declares its types.
        WorkflowNodeResourceTypes.ReadSourceDeclared(
                "GenericFhirSourceNode",
                """{"Resource types declared":true,"Resources":"Patient"}""")
            .Should().Equal("Patient");
        WorkflowNodeResourceTypes.ReadSourceDeclared(
                "GenericFhirSourceNode",
                """{"Resource types declared":false,"Resources":"Patient"}""")
            .Should().BeNull();
    }

    [Fact]
    public void ReadSourceDeclared_never_reads_the_retrieval_resource_type_even_when_marked()
    {
        WorkflowNodeResourceTypes.ReadSourceDeclared(
                "EpicSourceNode",
                Fields((WorkflowNodeResourceTypes.SourceTypesDeclaredKey, "true"), ("Retrieval resource type", "Observation")))
            .Should().BeNull();
    }

    [Fact]
    public void ReadSourceDeclared_lets_a_tabular_node_declare_without_the_marker()
    {
        // tab_streams, else tab_templates, else Resources — and a node is tabular by its type or by tab_kind, as the
        // portal decides it.
        WorkflowNodeResourceTypes.ReadSourceDeclared("TabularSourceNode", Fields(("tab_kind", "sql"), ("tab_streams", "[]"), ("Resources", "Procedure")))
            .Should().Equal("Procedure");
        WorkflowNodeResourceTypes.ReadSourceDeclared(
                "SomeSourceNode",
                Fields(("tab_kind", "csv"), ("tab_streams", JsonSerializer.Serialize(new[] { new { resourceType = "Immunization" } }))))
            .Should().Equal("Immunization");
        WorkflowNodeResourceTypes.ReadSourceDeclared("TabularSourceNode", Fields(("tab_kind", "sql"), ("tab_streams", "not json")))
            .Should().BeNull();
    }

    [Fact]
    public void A_legacy_generic_fhir_node_with_the_silent_12_type_list_feeds_an_organization_destination()
    {
        // Saved before sources declared their types: the Generic FHIR form filled "Resources" (and its hidden
        // retrieval field) with its 12 defaults, and no marker. Organization is not among them, but the list was
        // never the admin's choice, so re-saving the unchanged workflow is accepted.
        var source = new Node("src", "GenericFhirSourceNode", WorkflowNodeCategory.Source, "Hospital FHIR",
            Fields(
                ("Connector", "Generic FHIR R4"),
                ("Resources", GenericFhirSilentDefault),
                ("Retrieval resource type", GenericFhirSilentDefault)));

        WorkflowResourceTypeSubsetRule.Check([source, Destination("dst", "Patient, Organization", name: "Aidbox")], [new Edge("src", "dst")])
            .Should().BeNull();
    }

    [Fact]
    public void A_declared_generic_fhir_node_is_checked_rejecting_an_unread_type_and_accepting_a_subset()
    {
        var source = new Node("src", "GenericFhirSourceNode", WorkflowNodeCategory.Source, "Hospital FHIR",
            Fields((WorkflowNodeResourceTypes.SourceTypesDeclaredKey, "true"), ("Resources", GenericFhirSilentDefault)));

        WorkflowResourceTypeSubsetRule.Check([source, Destination("dst", "Patient, Organization", name: "Aidbox")], [new Edge("src", "dst")])
            .Should().Be(
                "Destination 'Aidbox' writes Organization, which its source 'Hospital FHIR' does not read. " +
                "Add it to the source's resource types or remove it from the destination.");
        WorkflowResourceTypeSubsetRule.Check([source, Destination("dst", "Patient, Observation", name: "Aidbox")], [new Edge("src", "dst")])
            .Should().BeNull();
    }

    [Fact]
    public void A_merge_fed_by_a_declaring_and_a_legacy_source_is_skipped_and_checked_once_both_declare()
    {
        // The portal's own case (upstream-source-v2.util.spec.ts), worded the same.
        Node[] nodes =
        [
            EhrSource("s1", "Patient", name: "Epic"),
            new Node("s2", "AthenahealthSourceNode", WorkflowNodeCategory.Source, "athena", Fields(("Connector", "Athenahealth"))),
            new Node("mg", "MergeNode", WorkflowNodeCategory.Transform, "Merge", "{}"),
            Destination("d1", "Patient, Procedure", name: "d1"),
        ];
        Edge[] edges = [new Edge("s1", "mg"), new Edge("s2", "mg"), new Edge("mg", "d1")];

        WorkflowResourceTypeSubsetRule.Check(nodes, edges).Should().BeNull();

        nodes[1] = EhrSource("s2", "Condition", name: "athena");
        WorkflowResourceTypeSubsetRule.Check(nodes, edges).Should().Be(
            "Destination 'd1' writes Procedure, which its sources 'Epic' and 'athena' do not read. " +
            "Add it to the source's resource types or remove it from the destination.");
    }
}
