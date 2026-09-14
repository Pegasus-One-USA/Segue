using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Moq;
using ResourceEnvelope = FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

// Covers the "only fetch what a destination actually consumes" fix: a source node's own "Resources" field can list
// more resource types than any downstream destination selects (e.g. a destination wizard narrowed to just
// "Patient"), and the source must not over-fetch (or, upstream of this, over-request OAuth scopes for) the rest.
public sealed class SourceNodeExecutorDestinationRestrictionTests
{
    [Fact]
    public async Task Source_fetch_is_restricted_to_reachable_destination_resource_types()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId);

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var typesFetched = new List<string>();
        var client = new Mock<IFhirSourceClient>();
        client
            .Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns((string type, FhirSourceConfiguration _, CancellationToken _) =>
            {
                typesFetched.Add(type);
                return Task.FromResult<IReadOnlyList<ResourceEnvelope>>([new ResourceEnvelope(type, "id-1", "{}", null, null)]);
            });

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var workflowStore = new InMemoryWorkflowDefinitionStore(TestHelpers.LicenseTestScopeFactory.Create());
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "restriction-test", 1);
        var sourceNode = workflow.AddNode(
            WorkflowNodeTypes.EpicSource,
            WorkflowNodeCategory.Source,
            rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{sourceConnectionId}}","Resources":"Patient, Observation, Condition"}""");
        var destinationNode = workflow.AddNode(
            WorkflowNodeTypes.SqlServerDestination,
            WorkflowNodeCategory.Destination,
            rank: 1,
            configurationJson: """{"dest_resources":"Patient"}""");
        workflow.AddEdge(sourceNode.Id, destinationNode.Id);
        await workflowStore.SaveAsync(workflow, CancellationToken.None);

        var executor = new EpicSourceNodeExecutor(
            clientFactory.Object, resolver.Object, workflowDefinitionStore: workflowStore);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var output = await executor.ExecuteAsync(context, sourceNode, [], CancellationToken.None);

        typesFetched.Should().BeEquivalentTo(["Patient"]);
        output.Payload.Should().BeOfType<ResourceBatch>().Which.Resources.Should().HaveCount(1);
    }

    [Fact]
    public async Task Non_compartment_type_referenced_by_a_selected_resource_is_excluded_when_not_itself_selected()
    {
        // Reverted behavior, explicitly requested by the user over the earlier auto-reference-resolution fix:
        // manual selection (dest_resources) is the ONLY thing that controls what gets fetched. An Encounter
        // referencing Location/loc1 must NOT cause Location to be fetched unless "Location" is itself checked in
        // a reachable destination's dest_resources — even though that can leave a dangling reference at the
        // destination (surfaced instead as a pre-write warning by MappedFhirRepositoryDestinationWriter).
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId);

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var encounterJson = """{"resourceType":"Encounter","id":"e1","location":[{"location":{"reference":"Location/loc1"}}]}""";
        var client = new Mock<IFhirSourceClient>();
        client
            .Setup(x => x.SearchAsync("Patient", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Patient", "p1", "{}", null, null)]);
        client
            .Setup(x => x.SearchAsync("Encounter", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Encounter", "e1", encounterJson, null, null)]);

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var workflowStore = new InMemoryWorkflowDefinitionStore(TestHelpers.LicenseTestScopeFactory.Create());
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "reference-resolved-restriction-test", 1);
        var sourceNode = workflow.AddNode(
            WorkflowNodeTypes.EpicSource,
            WorkflowNodeCategory.Source,
            rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{sourceConnectionId}}","Resources":"Patient, Encounter, Location"}""");
        // Deliberately omits "Location" — Location must stay excluded even though Encounter references it.
        var destinationNode = workflow.AddNode(
            WorkflowNodeTypes.FhirRepositoryDestination,
            WorkflowNodeCategory.Destination,
            rank: 1,
            configurationJson: """{"dest_resources":"Patient,Encounter"}""");
        workflow.AddEdge(sourceNode.Id, destinationNode.Id);
        await workflowStore.SaveAsync(workflow, CancellationToken.None);

        var executor = new EpicSourceNodeExecutor(
            clientFactory.Object, resolver.Object, workflowDefinitionStore: workflowStore);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var output = await executor.ExecuteAsync(context, sourceNode, [], CancellationToken.None);

        client.Verify(x => x.SearchAsync("Location", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);

        var resources = output.Payload.Should().BeOfType<ResourceBatch>().Subject.Resources;
        resources.Should().NotContain(r => r.ResourceType == "Location");
    }

    [Fact]
    public async Task Non_compartment_type_is_fetched_via_a_single_clean_unscoped_request_when_explicitly_selected()
    {
        // The user's chosen tradeoff: checking "Location" in a reachable destination's dest_resources fetches it
        // directly — one bare request, not scoped by the Patient cohort (Epic has no patient= search for Location)
        // and not carrying forward the Patient search's own SearchParameters/PatientSearchCriteria (that reuse was
        // the actual cause of the historical 400s, not the act of fetching the type at all).
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId,
            SearchParameters: "identifier=MRN12345");

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var client = new Mock<IFhirSourceClient>();
        client
            .Setup(x => x.SearchAsync("Patient", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Patient", "p1", "{}", null, null), new ResourceEnvelope("Patient", "p2", "{}", null, null)]);
        client
            .Setup(x => x.SearchAsync("Location", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Location", "loc1", """{"resourceType":"Location","id":"loc1"}""", null, null)]);

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var workflowStore = new InMemoryWorkflowDefinitionStore(TestHelpers.LicenseTestScopeFactory.Create());
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "explicit-non-compartment-selection-test", 1);
        var sourceNode = workflow.AddNode(
            WorkflowNodeTypes.EpicSource,
            WorkflowNodeCategory.Source,
            rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{sourceConnectionId}}","Resources":"Patient, Location"}""");
        var destinationNode = workflow.AddNode(
            WorkflowNodeTypes.FhirRepositoryDestination,
            WorkflowNodeCategory.Destination,
            rank: 1,
            configurationJson: """{"dest_resources":"Patient,Location"}""");
        workflow.AddEdge(sourceNode.Id, destinationNode.Id);
        await workflowStore.SaveAsync(workflow, CancellationToken.None);

        var executor = new EpicSourceNodeExecutor(
            clientFactory.Object, resolver.Object, workflowDefinitionStore: workflowStore);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var output = await executor.ExecuteAsync(context, sourceNode, [], CancellationToken.None);

        // Exactly one Location request total (not one per cohort patient), and it carries no leftover
        // Patient-search criteria (SearchParameters/PatientIds are cleared, not the "identifier=MRN12345" above).
        client.Verify(
            x => x.SearchAsync(
                "Location",
                It.Is<FhirSourceConfiguration>(cfg => cfg.SearchParameters == null && (cfg.PatientIds == null || cfg.PatientIds.Count == 0)),
                It.IsAny<CancellationToken>()),
            Times.Once);

        var resources = output.Payload.Should().BeOfType<ResourceBatch>().Subject.Resources;
        resources.Should().Contain(r => r.ResourceType == "Location" && r.ResourceId == "loc1");
    }

    [Fact]
    public async Task Source_fetch_is_unrestricted_when_no_destination_is_reachable()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId);

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var typesFetched = new List<string>();
        var client = new Mock<IFhirSourceClient>();
        client
            .Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns((string type, FhirSourceConfiguration _, CancellationToken _) =>
            {
                typesFetched.Add(type);
                return Task.FromResult<IReadOnlyList<ResourceEnvelope>>([new ResourceEnvelope(type, "id-1", "{}", null, null)]);
            });

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var workflowStore = new InMemoryWorkflowDefinitionStore(TestHelpers.LicenseTestScopeFactory.Create());
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "no-destination-test", 1);
        var sourceNode = workflow.AddNode(
            WorkflowNodeTypes.EpicSource,
            WorkflowNodeCategory.Source,
            rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{sourceConnectionId}}","Resources":"Patient, Observation, Condition"}""");
        await workflowStore.SaveAsync(workflow, CancellationToken.None);

        var executor = new EpicSourceNodeExecutor(
            clientFactory.Object, resolver.Object, workflowDefinitionStore: workflowStore);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var output = await executor.ExecuteAsync(context, sourceNode, [], CancellationToken.None);

        typesFetched.Should().BeEquivalentTo(["Patient", "Observation", "Condition"]);
        output.Payload.Should().BeOfType<ResourceBatch>().Which.Resources.Should().HaveCount(3);
    }
}
