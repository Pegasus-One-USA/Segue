using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Sources;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.SharedKernel.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Sources;

public sealed class EpicSourceConnectionScopeSyncServiceTests
{
    private readonly Mock<IWorkflowDefinitionStore> _workflowStore = new();
    private readonly Mock<IConfigurationRepository> _configurationRepository = new();
    private readonly ScopeGeneratorService _scopeGenerator = new();

    private EpicSourceConnectionScopeSyncService Service() => new(
        _workflowStore.Object,
        _configurationRepository.Object,
        _scopeGenerator,
        NullLogger<EpicSourceConnectionScopeSyncService>.Instance);

    private static SourceConnection MakeSource(string[] scopes) => new(
        "Epic",
        SourceSystemType.Epic,
        "https://fhir.example.com",
        new SourceAuthenticationConfiguration(AuthenticationType.None, "client-1", null, scopes, null, null, null),
        applicationType: ApplicationType.Standalone,
        interactive: new SourceInteractiveConfiguration(["https://app.example.com/oauth/callback"], null, []));

    private static SourceConnection MakeBackendSource(string[] scopes) => new(
        "Backend",
        SourceSystemType.Epic,
        "https://fhir.example.com",
        new SourceAuthenticationConfiguration(AuthenticationType.None, "client-2", null, scopes, null, null, null),
        applicationType: ApplicationType.Backend,
        interactive: null);

    private static SourceConnection MakeAthenaPatientSource(string[] scopes) => new(
        "Athena Patient",
        SourceSystemType.Athenahealth,
        "https://api.preview.platform.athenahealth.com/fhir/r4",
        new SourceAuthenticationConfiguration(AuthenticationType.None, "client-3", null, scopes, null, null, null),
        applicationType: ApplicationType.Patient,
        interactive: new SourceInteractiveConfiguration(["http://localhost:5000/api/v1/oauth/callback"], null, []));

    private static WorkflowDefinition WorkflowWithAutoFetchDestination(
        Guid sourceConnectionId, string destResources)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "wf", 1);
        var source = workflow.AddNode(
            "AthenahealthSourceNode", WorkflowNodeCategory.Source, rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{sourceConnectionId}}"}""");
        var destination = workflow.AddNode(
            "FhirRepositoryDestinationNode", WorkflowNodeCategory.Destination, rank: 30,
            configurationJson: $$"""{"dest_resources":"{{destResources}}","dest_autoFetchMissingReferences":"true"}""");
        workflow.AddEdge(source.Id, destination.Id);
        return workflow;
    }

    private static WorkflowDefinition WorkflowWithEpicSourceAndDestination(
        Guid sourceConnectionId, string destResources)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "wf", 1);
        var source = workflow.AddNode(
            "EpicSourceNode", WorkflowNodeCategory.Source, rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{sourceConnectionId}}"}""");
        var destination = workflow.AddNode(
            "SqlServerDestinationNode", WorkflowNodeCategory.Destination, rank: 30,
            configurationJson: $$"""{"dest_resources":"{{destResources}}"}""");
        workflow.AddEdge(source.Id, destination.Id);
        return workflow;
    }

    // A source node that declares the resource types it reads ("Resources", marked "Resource types declared"),
    // feeding one destination.
    private static WorkflowDefinition WorkflowWithDeclaringSource(
        Guid sourceConnectionId, string sourceResources, string destResources, bool autoFetch = false)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "wf", 1);
        var source = workflow.AddNode(
            "EpicSourceNode", WorkflowNodeCategory.Source, rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{sourceConnectionId}}","Resource types declared":"true","Resources":"{{sourceResources}}"}""");
        var destination = workflow.AddNode(
            "FhirRepositoryDestinationNode", WorkflowNodeCategory.Destination, rank: 30,
            configurationJson: $$"""{"dest_resources":"{{destResources}}","dest_autoFetchMissingReferences":"{{(autoFetch ? "true" : "false")}}"}""");
        workflow.AddEdge(source.Id, destination.Id);
        return workflow;
    }

    private void SetUpSyncableSource(SourceConnection source, params WorkflowDefinition[] workflows)
    {
        _configurationRepository.Setup(x => x.GetSourceConnectionAsync(source.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source);
        _configurationRepository
            .Setup(x => x.UpdateSourceConnectionAsync(It.IsAny<SourceConnection>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _workflowStore.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(workflows);
    }

    [Fact]
    public async Task Scopes_follow_the_source_nodes_declared_resource_types_not_the_destinations()
    {
        // The source reads Patient, Observation, Condition; its destination only writes Patient. The connection is
        // scoped for what the source reads — the destination chooses from that list, it does not define it.
        var source = MakeSource(["openid", "fhirUser", "offline_access", "user/Patient.rs", "user/MedicationRequest.rs"]);
        SetUpSyncableSource(source, WorkflowWithDeclaringSource(source.Id, "Patient,Observation,Condition", "Patient"));

        var result = await Service().SyncAsync(source.Id, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Should().Contain(["user/Patient.rs", "user/Observation.rs", "user/Condition.rs"]);
        result.Should().NotContain("user/MedicationRequest.rs");
    }

    [Fact]
    public async Task Declared_types_are_unioned_across_workflows_and_a_legacy_source_falls_back_to_its_destinations()
    {
        var source = MakeSource(["openid", "fhirUser", "offline_access", "user/Patient.rs"]);
        SetUpSyncableSource(
            source,
            WorkflowWithDeclaringSource(source.Id, "Patient,Observation", "Patient"),
            // Legacy source node: no declared list, so its own destination's selection stands in for it.
            WorkflowWithEpicSourceAndDestination(source.Id, "Condition"));

        var result = await Service().SyncAsync(source.Id, CancellationToken.None);

        result!.Should().Contain(["user/Patient.rs", "user/Observation.rs", "user/Condition.rs"]);
    }

    [Fact]
    public async Task A_legacy_source_only_falls_back_to_the_destinations_reachable_from_it()
    {
        // Two sources in one workflow: this connection's legacy source feeds a Patient destination; another
        // connection's source feeds an AllergyIntolerance destination. Only the reachable destination counts.
        var source = MakeSource(["openid", "fhirUser", "offline_access", "user/Patient.rs"]);
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "wf", 1);
        var ours = workflow.AddNode(
            "EpicSourceNode", WorkflowNodeCategory.Source, rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{source.Id}}"}""");
        var theirs = workflow.AddNode(
            "CernerSourceNode", WorkflowNodeCategory.Source, rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{Guid.NewGuid()}}"}""");
        var ourDestination = workflow.AddNode(
            "SqlServerDestinationNode", WorkflowNodeCategory.Destination, rank: 30,
            configurationJson: """{"dest_resources":"Patient"}""");
        var theirDestination = workflow.AddNode(
            "SqlServerDestinationNode", WorkflowNodeCategory.Destination, rank: 30,
            configurationJson: """{"dest_resources":"AllergyIntolerance"}""");
        workflow.AddEdge(ours.Id, ourDestination.Id);
        workflow.AddEdge(theirs.Id, theirDestination.Id);
        SetUpSyncableSource(source, workflow);

        var result = await Service().SyncAsync(source.Id, CancellationToken.None);

        result!.Should().Contain("user/Patient.rs");
        result.Should().NotContain("user/AllergyIntolerance.rs");
    }

    [Fact]
    public async Task A_declaring_source_still_widens_for_a_reachable_auto_fetch_destination()
    {
        var source = MakeSource(["openid", "fhirUser", "offline_access", "user/Patient.rs"]);
        SetUpSyncableSource(source, WorkflowWithDeclaringSource(source.Id, "Patient,Observation", "Patient", autoFetch: true));

        var result = await Service().SyncAsync(source.Id, CancellationToken.None);

        result!.Should().Contain(["user/Patient.rs", "user/Observation.rs", "user/Organization.rs", "user/Practitioner.rs"]);
    }

    // Every type the pre-step-B EHR form silently pre-filled the hidden retrieval control with (Epic and others).
    private const string SilentFullRetrievalDefault =
        "AllergyIntolerance,CarePlan,Condition,DiagnosticReport,DocumentReference,Encounter,Immunization," +
        "MedicationRequest,Observation,Patient,Procedure";

    // A source node saved before sources declared their types: the EHR form wrote only its hidden
    // "Retrieval resource type" (never "Resources"), and the destinations decided what was scoped.
    private static WorkflowDefinition WorkflowWithLegacyRetrievalOnlySource(
        Guid sourceConnectionId, string nodeType, string retrievalResourceTypes, string destResources)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "wf", 1);
        var source = workflow.AddNode(
            nodeType, WorkflowNodeCategory.Source, rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{sourceConnectionId}}","Retrieval resource type":"{{retrievalResourceTypes}}"}""");
        var destination = workflow.AddNode(
            "SqlServerDestinationNode", WorkflowNodeCategory.Destination, rank: 30,
            configurationJson: $$"""{"dest_resources":"{{destResources}}"}""");
        workflow.AddEdge(source.Id, destination.Id);
        return workflow;
    }

    private static SourceConnection MakeAthenaBackendSource(string[] scopes, string[] retrievalResourceTypes) => new(
        "Athena Backend",
        SourceSystemType.Athenahealth,
        "https://api.preview.platform.athenahealth.com/fhir/r4",
        new SourceAuthenticationConfiguration(AuthenticationType.None, "client-5", null, scopes, null, null, null),
        applicationType: ApplicationType.Backend,
        interactive: null,
        retrieval: new SourceRetrievalConfiguration("search-rest", retrievalResourceTypes, null, false));

    [Fact]
    public async Task A_legacy_epic_node_with_the_silent_full_retrieval_default_is_scoped_by_its_destinations()
    {
        var source = MakeSource(["openid", "fhirUser", "offline_access", "user/Patient.rs"]);
        SetUpSyncableSource(
            source,
            WorkflowWithLegacyRetrievalOnlySource(source.Id, "EpicSourceNode", SilentFullRetrievalDefault, "Patient"));

        var result = await Service().SyncAsync(source.Id, CancellationToken.None);

        result!.Where(scope => scope.Contains('/')).Should().Equal("user/Patient.rs");
    }

    [Fact]
    public async Task A_legacy_athena_backend_node_with_a_stale_cloned_retrieval_list_keeps_destination_driven_scopes()
    {
        // Cloned when the connection read Patient,Encounter; its destination has since moved to Patient,Observation.
        var source = MakeAthenaBackendSource(["system/Patient.read", "system/Encounter.read"], ["Patient", "Encounter"]);
        SetUpSyncableSource(
            source,
            WorkflowWithLegacyRetrievalOnlySource(source.Id, "AthenahealthSourceNode", "Patient,Encounter", "Patient,Observation"));

        var result = await Service().SyncAsync(source.Id, CancellationToken.None);

        result!.Should().Contain(["system/Patient.read", "system/Observation.read"]);
        result.Should().NotContain(scope => scope.Contains("Encounter"));
        source.Retrieval!.ResourceTypes.Should().Equal("Observation", "Patient");
    }

    [Fact]
    public async Task A_legacy_source_is_scoped_for_the_types_its_destinations_map_as_well_as_select()
    {
        // The portal's build scopes a legacy source for dest_resources plus every dest_mappings row's resource
        // (destinationWrittenResourceTypes); the sync must use the same union, or it would drop Encounter for
        // athenahealth right after the build.
        var source = MakeAthenaBackendSource(["system/Patient.read", "system/Encounter.read"], ["Encounter", "Patient"]);
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "wf", 1);
        var legacy = workflow.AddNode(
            "AthenahealthSourceNode", WorkflowNodeCategory.Source, rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{source.Id}}"}""");
        var destination = workflow.AddNode(
            "SqlServerDestinationNode", WorkflowNodeCategory.Destination, rank: 30,
            configurationJson: """{"dest_resources":"Patient","dest_mappings":"[{\"resource\":\"Encounter\",\"jsonPath\":\"$.id\"}]"}""");
        workflow.AddEdge(legacy.Id, destination.Id);
        SetUpSyncableSource(source, workflow);

        var result = await Service().SyncAsync(source.Id, CancellationToken.None);

        result!.Where(scope => scope.Contains('/')).Should().Equal("system/Encounter.read", "system/Patient.read");
        source.Retrieval!.ResourceTypes.Should().Equal("Encounter", "Patient");
    }

    [Fact]
    public async Task Two_legacy_sources_merging_into_one_destination_both_count_it()
    {
        // S1 and S2 (both this connection) feed one merge node in front of D. Each source reaches D, so each is
        // scoped for it — the portal's build attributes D to both as well (not only to the first inbound edge's).
        var source = MakeAthenaBackendSource(["system/Patient.read"], ["Patient"]);
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "wf", 1);
        var first = workflow.AddNode(
            "AthenahealthSourceNode", WorkflowNodeCategory.Source, rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{source.Id}}"}""");
        var second = workflow.AddNode(
            "AthenahealthSourceNode", WorkflowNodeCategory.Source, rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{Guid.NewGuid()}}"}""");
        var merge = workflow.AddNode("MergeNode", WorkflowNodeCategory.Transform, rank: 10, configurationJson: "{}");
        var destination = workflow.AddNode(
            "SqlServerDestinationNode", WorkflowNodeCategory.Destination, rank: 30,
            configurationJson: """{"dest_resources":"Patient,Condition"}""");
        workflow.AddEdge(second.Id, merge.Id);
        workflow.AddEdge(first.Id, merge.Id);
        workflow.AddEdge(merge.Id, destination.Id);
        SetUpSyncableSource(source, workflow);

        var result = await Service().SyncAsync(source.Id, CancellationToken.None);

        result!.Where(scope => scope.Contains('/')).Should().Equal("system/Condition.read", "system/Patient.read");
    }

    [Fact]
    public async Task A_legacy_athena_backend_node_with_the_full_cloned_fallback_is_not_widened_to_every_type()
    {
        var source = MakeAthenaBackendSource(["system/Patient.read"], ["Patient"]);
        SetUpSyncableSource(
            source,
            WorkflowWithLegacyRetrievalOnlySource(source.Id, "AthenahealthSourceNode", SilentFullRetrievalDefault, "Patient"));

        var result = await Service().SyncAsync(source.Id, CancellationToken.None);

        result!.Where(scope => scope.Contains('/')).Should().Equal("system/Patient.read");
        source.Retrieval!.ResourceTypes.Should().Equal("Patient");
    }

    [Fact]
    public async Task A_legacy_healow_interactive_node_with_the_full_cloned_fallback_is_scoped_by_its_destinations()
    {
        var source = new SourceConnection(
            "eCW",
            SourceSystemType.Healow,
            "https://fhir4.healow.com/fhir/r4/ABC",
            new SourceAuthenticationConfiguration(AuthenticationType.None, "client-6", null, ["user/Patient.read"], null, null, null),
            applicationType: ApplicationType.Standalone,
            interactive: new SourceInteractiveConfiguration(["https://app.example.com/oauth/callback"], null, []));
        SetUpSyncableSource(
            source,
            WorkflowWithLegacyRetrievalOnlySource(source.Id, "EClinicalWorksSourceNode", SilentFullRetrievalDefault, "Patient,Condition"));

        var result = await Service().SyncAsync(source.Id, CancellationToken.None);

        result!.Should().Contain(scope => scope.EndsWith("/Patient.read"));
        result.Should().Contain(scope => scope.EndsWith("/Condition.read"));
        result.Should().NotContain(scope => scope.Contains("Observation") || scope.Contains("Procedure") || scope.Contains("Encounter"));
    }

    [Fact]
    public async Task A_legacy_source_with_no_edges_still_counts_every_destination_in_its_workflow()
    {
        // A workflow saved without edges: before sources declared their types every destination in it counted, and
        // re-syncing it must not narrow the shared connection's scopes.
        var source = MakeSource(["openid", "fhirUser", "offline_access", "user/Patient.rs", "user/Observation.rs"]);
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "wf", 1);
        workflow.AddNode(
            "EpicSourceNode", WorkflowNodeCategory.Source, rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{source.Id}}"}""");
        workflow.AddNode(
            "SqlServerDestinationNode", WorkflowNodeCategory.Destination, rank: 30,
            configurationJson: """{"dest_resources":"Patient,Observation"}""");
        SetUpSyncableSource(source, workflow);

        var result = await Service().SyncAsync(source.Id, CancellationToken.None);

        result!.Where(scope => scope.Contains('/')).Should().Equal("user/Observation.rs", "user/Patient.rs");
    }

    [Fact]
    public async Task A_legacy_and_a_declaring_source_on_one_connection_in_one_workflow_are_unioned()
    {
        var source = MakeSource(["openid", "fhirUser", "offline_access", "user/Patient.rs"]);
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "wf", 1);
        var legacy = workflow.AddNode(
            "EpicSourceNode", WorkflowNodeCategory.Source, rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{source.Id}}","Retrieval resource type":"{{SilentFullRetrievalDefault}}"}""");
        var declaring = workflow.AddNode(
            "EpicSourceNode", WorkflowNodeCategory.Source, rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{source.Id}}","Resource types declared":"true","Resources":"Condition"}""");
        var legacyDestination = workflow.AddNode(
            "SqlServerDestinationNode", WorkflowNodeCategory.Destination, rank: 30,
            configurationJson: """{"dest_resources":"Patient"}""");
        var declaringDestination = workflow.AddNode(
            "SqlServerDestinationNode", WorkflowNodeCategory.Destination, rank: 30,
            configurationJson: """{"dest_resources":"Condition"}""");
        workflow.AddEdge(legacy.Id, legacyDestination.Id);
        workflow.AddEdge(declaring.Id, declaringDestination.Id);
        SetUpSyncableSource(source, workflow);

        var result = await Service().SyncAsync(source.Id, CancellationToken.None);

        result!.Where(scope => scope.Contains('/')).Should().Equal("user/Condition.rs", "user/Patient.rs");
    }

    [Fact]
    public async Task A_declared_type_the_vendor_does_not_support_is_left_out_of_the_scopes()
    {
        // Task is not in athenahealth's supported list; one unregistered scope fails the whole token request.
        var source = MakeAthenaBackendSource(["system/Patient.read"], ["Patient"]);
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "wf", 1);
        var node = workflow.AddNode(
            "AthenahealthSourceNode", WorkflowNodeCategory.Source, rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{source.Id}}","Resource types declared":"true","Resources":"Patient,Task"}""");
        var destination = workflow.AddNode(
            "SqlServerDestinationNode", WorkflowNodeCategory.Destination, rank: 30,
            configurationJson: """{"dest_resources":"Patient"}""");
        workflow.AddEdge(node.Id, destination.Id);
        SetUpSyncableSource(source, workflow);

        var result = await Service().SyncAsync(source.Id, CancellationToken.None);

        result!.Should().Contain("system/Patient.read");
        result.Should().NotContain(scope => scope.Contains("Task"));
        source.Retrieval!.ResourceTypes.Should().Equal("Patient");
    }

    // The 12 types the pre-step-B Generic FHIR form saved into "Resources" without the admin choosing them.
    private const string GenericFhirSilentDefault =
        "Patient,Practitioner,Encounter,AllergyIntolerance,Observation,Condition,Procedure,ServiceRequest," +
        "DiagnosticReport,MedicationRequest,MedicationAdministration,Provenance";

    private static WorkflowDefinition WorkflowWithSourceFields(
        Guid sourceConnectionId, string nodeType, string extraSourceFields, string destResources)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "wf", 1);
        var source = workflow.AddNode(
            nodeType, WorkflowNodeCategory.Source, rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{sourceConnectionId}}",{{extraSourceFields}}}""");
        var destination = workflow.AddNode(
            "FhirRepositoryDestinationNode", WorkflowNodeCategory.Destination, rank: 30,
            configurationJson: $$"""{"dest_resources":"{{destResources}}"}""");
        workflow.AddEdge(source.Id, destination.Id);
        return workflow;
    }

    [Fact]
    public async Task A_legacy_generic_fhir_node_with_the_silent_12_type_list_is_scoped_by_its_destinations()
    {
        // No "Resource types declared" marker: its "Resources" is the form's silent default, not a declaration,
        // so the connection is scoped for what the destination writes (Organization included), not the 12 types.
        var source = MakeSource(["openid", "fhirUser", "offline_access", "user/Patient.rs"]);
        SetUpSyncableSource(
            source,
            WorkflowWithSourceFields(
                source.Id, "GenericFhirSourceNode", $"\"Resources\":\"{GenericFhirSilentDefault}\"", "Patient,Organization"));

        var result = await Service().SyncAsync(source.Id, CancellationToken.None);

        result!.Where(scope => scope.Contains('/')).Should().Equal("user/Organization.rs", "user/Patient.rs");
    }

    [Theory]
    [InlineData("\"Resources\":\"Patient,Condition\"", "user/Organization.rs", "user/Patient.rs")]
    [InlineData("\"Resource types declared\":\"false\",\"Resources\":\"Patient,Condition\"", "user/Organization.rs", "user/Patient.rs")]
    [InlineData("\"Resource types declared\":\"true\",\"Resources\":\"Patient,Condition\"", "user/Condition.rs", "user/Patient.rs")]
    [InlineData("\"Resource types declared\":\"True\",\"Resources\":\"Patient,Condition\"", "user/Condition.rs", "user/Patient.rs")]
    public async Task Only_a_source_marked_as_declaring_its_types_drives_the_scopes(
        string sourceFields, string firstScope, string secondScope)
    {
        var source = MakeSource(["openid", "fhirUser", "offline_access", "user/Patient.rs"]);
        SetUpSyncableSource(source, WorkflowWithSourceFields(source.Id, "EpicSourceNode", sourceFields, "Patient,Organization"));

        var result = await Service().SyncAsync(source.Id, CancellationToken.None);

        result!.Where(scope => scope.Contains('/')).Should().Equal(firstScope, secondScope);
    }

    [Fact]
    public async Task Backend_services_connection_is_left_untouched()
    {
        var source = MakeBackendSource(["system/Patient.rs"]);

        _configurationRepository.Setup(x => x.GetSourceConnectionAsync(source.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source);

        var result = await Service().SyncAsync(source.Id, CancellationToken.None);

        result.Should().BeNull();
        _workflowStore.Verify(x => x.ListAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Unknown_connection_returns_null()
    {
        _configurationRepository.Setup(x => x.GetSourceConnectionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SourceConnection?)null);

        var result = await Service().SyncAsync(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Scopes_are_derived_as_the_union_across_every_workflow_sharing_the_connection()
    {
        var source = MakeSource(["openid", "fhirUser", "offline_access", "launch/patient",
            "user/Patient.rs", "user/Observation.rs", "user/Condition.rs", "user/MedicationRequest.rs", "user/AllergyIntolerance.rs"]);

        _configurationRepository.Setup(x => x.GetSourceConnectionAsync(source.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source);
        _configurationRepository
            .Setup(x => x.UpdateSourceConnectionAsync(source, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Workflow A only needs Patient; workflow B needs Patient, Observation, Condition. The connection should
        // end up scoped to the union — Patient, Observation, Condition — not either workflow's list alone, and
        // NOT the stale broader set (MedicationRequest/AllergyIntolerance/launch-patient) it started with.
        var workflowA = WorkflowWithEpicSourceAndDestination(source.Id, "Patient");
        var workflowB = WorkflowWithEpicSourceAndDestination(source.Id, "Patient,Observation,Condition");
        var unrelatedWorkflow = WorkflowWithEpicSourceAndDestination(Guid.NewGuid(), "MedicationRequest");

        _workflowStore.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([workflowA, workflowB, unrelatedWorkflow]);

        var result = await Service().SyncAsync(source.Id, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Should().Contain(["user/Patient.rs", "user/Observation.rs", "user/Condition.rs"]);
        result.Should().NotContain("user/MedicationRequest.rs");
        result.Should().NotContain("user/AllergyIntolerance.rs");
        // Provider Standalone: never launch/patient (the original bug), regardless of usage.
        result.Should().NotContain("launch/patient");

        source.Authentication.Scopes.Should().BeEquivalentTo(result);
        _configurationRepository.Verify(
            x => x.UpdateSourceConnectionAsync(source, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task No_workflow_references_the_connection_yields_empty_scopes_and_still_persists()
    {
        var source = MakeSource(["openid", "fhirUser", "offline_access", "user/Patient.rs"]);
        _configurationRepository.Setup(x => x.GetSourceConnectionAsync(source.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source);
        _configurationRepository
            .Setup(x => x.UpdateSourceConnectionAsync(source, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _workflowStore.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var result = await Service().SyncAsync(source.Id, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Where(s => s.Contains('/')).Should().BeEmpty();
    }

    [Fact]
    public async Task Already_in_sync_does_not_call_update()
    {
        var source = MakeSource(["openid", "fhirUser", "offline_access", "user/Patient.rs"]);
        _configurationRepository.Setup(x => x.GetSourceConnectionAsync(source.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source);

        var workflow = WorkflowWithEpicSourceAndDestination(source.Id, "Patient");
        _workflowStore.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([workflow]);

        var result = await Service().SyncAsync(source.Id, CancellationToken.None);

        result.Should().BeEquivalentTo(source.Authentication.Scopes);
        _configurationRepository.Verify(
            x => x.UpdateSourceConnectionAsync(It.IsAny<SourceConnection>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncAllAsync_only_touches_interactive_connections_and_reports_changed_ids()
    {
        var driftedSource = MakeSource(["openid", "fhirUser", "offline_access", "launch/patient", "user/Patient.rs", "user/Observation.rs"]);
        var alreadyCorrectSource = MakeSource(["openid", "fhirUser", "offline_access", "user/Patient.rs"]);
        var backendSource = MakeBackendSource(["system/Patient.rs"]);

        _configurationRepository.Setup(x => x.GetSourceConnectionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([driftedSource, alreadyCorrectSource, backendSource]);
        _configurationRepository.Setup(x => x.GetSourceConnectionAsync(driftedSource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(driftedSource);
        _configurationRepository.Setup(x => x.GetSourceConnectionAsync(alreadyCorrectSource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(alreadyCorrectSource);
        _configurationRepository
            .Setup(x => x.UpdateSourceConnectionAsync(It.IsAny<SourceConnection>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _workflowStore.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                WorkflowWithEpicSourceAndDestination(driftedSource.Id, "Patient,Observation"),
                WorkflowWithEpicSourceAndDestination(alreadyCorrectSource.Id, "Patient"),
            ]);

        var changed = await Service().SyncAllAsync(CancellationToken.None);

        changed.Should().ContainSingle().Which.Should().Be(driftedSource.Id);
        _configurationRepository.Verify(
            x => x.GetSourceConnectionAsync(backendSource.Id, It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact]
    public async Task Patient_application_type_never_widens_to_reference_targets_even_with_auto_fetch_on()
    {
        // Regression: auto-fetch widening turned a Patient-only selection into
        // patient/Organization.read + patient/Practitioner.read + patient/RelatedPerson.read
        // (FhirReferenceTargets["Patient"]), which athenahealth rejects outright — access_denied
        // "Policy evaluation failed" — because its policy evaluation is all-or-nothing per request.
        var source = MakeAthenaPatientSource(["openid", "fhirUser", "offline_access", "launch/patient", "patient/Patient.read"]);

        _configurationRepository.Setup(x => x.GetSourceConnectionAsync(source.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source);
        _configurationRepository
            .Setup(x => x.UpdateSourceConnectionAsync(It.IsAny<SourceConnection>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _workflowStore.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([WorkflowWithAutoFetchDestination(source.Id, "Patient")]);

        var result = await Service().SyncAsync(source.Id, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Should().BeEquivalentTo(["openid", "fhirUser", "offline_access", "launch/patient", "patient/Patient.read"]);
        result.Should().NotContain(["patient/Organization.read", "patient/Practitioner.read", "patient/RelatedPerson.read"]);
    }

    [Fact]
    public async Task Non_patient_application_types_still_widen_to_reference_targets_when_auto_fetch_is_on()
    {
        // The guard above is scoped to ApplicationType.Patient only — Provider Standalone keeps the widening it
        // was built for, so auto-fetch can pull a referenced Organization/Practitioner without a 403.
        var source = MakeSource(["openid", "fhirUser", "offline_access", "user/Patient.rs"]);

        _configurationRepository.Setup(x => x.GetSourceConnectionAsync(source.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source);
        _configurationRepository
            .Setup(x => x.UpdateSourceConnectionAsync(It.IsAny<SourceConnection>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _workflowStore.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([WorkflowWithAutoFetchDestination(source.Id, "Patient")]);

        var result = await Service().SyncAsync(source.Id, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Should().Contain(["user/Patient.rs", "user/Organization.rs", "user/Practitioner.rs", "user/RelatedPerson.rs"]);
    }

    [Fact]
    public async Task Patient_application_type_keeps_every_resource_type_the_workflow_actually_selected()
    {
        // The guard drops only the speculative widening, never the operator's own selection.
        var source = MakeAthenaPatientSource(["openid", "fhirUser", "offline_access", "launch/patient", "patient/Patient.read"]);

        _configurationRepository.Setup(x => x.GetSourceConnectionAsync(source.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source);
        _configurationRepository
            .Setup(x => x.UpdateSourceConnectionAsync(It.IsAny<SourceConnection>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _workflowStore.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([WorkflowWithAutoFetchDestination(source.Id, "Patient,Observation")]);

        var result = await Service().SyncAsync(source.Id, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Should().Contain(["patient/Patient.read", "patient/Observation.read"]);
        result.Should().NotContain("patient/Organization.read");
    }

    [Fact]
    public async Task A_write_only_connection_is_never_synced()
    {
        // No source node ever references it, so a sync would regenerate its scopes from nothing and strip the ones
        // its write token is requested with.
        var writeOnly = new SourceConnection(
            "athena write",
            SourceSystemType.Athenahealth,
            "https://api.preview.platform.athenahealth.com/fhir/r4",
            new SourceAuthenticationConfiguration(AuthenticationType.None, "client-4", null, ["system/AllergyIntolerance.write"], null, null, null),
            applicationType: ApplicationType.Backend,
            access: SourceConnectionAccess.Write);

        _configurationRepository.Setup(x => x.GetSourceConnectionAsync(writeOnly.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(writeOnly);
        _configurationRepository.Setup(x => x.GetSourceConnectionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([writeOnly]);

        var result = await Service().SyncAsync(writeOnly.Id, CancellationToken.None);
        var changed = await Service().SyncAllAsync(CancellationToken.None);

        result.Should().BeNull();
        changed.Should().BeEmpty();
        writeOnly.Authentication.Scopes.Should().Equal("system/AllergyIntolerance.write");
        _workflowStore.Verify(x => x.ListAsync(It.IsAny<CancellationToken>()), Times.Never);
        _configurationRepository.Verify(
            x => x.UpdateSourceConnectionAsync(It.IsAny<SourceConnection>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
