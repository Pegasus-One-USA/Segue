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

// Covers the fix for Epic rejecting Practitioner/Organization/Medication search outright ("Only an _ID search is
// allowed" / "Either name, family, or identifier is a required parameter") — these three are now resolved by
// reading each id the rest of this node's batch actually references, not by searching.
public sealed class ReferenceScopedResourceFetchTests
{
    private static (Mock<IFhirSourceClient> Client, Mock<IFhirSourceClientFactory> Factory) BuildClientFactory()
    {
        var client = new Mock<IFhirSourceClient>();
        var factory = new Mock<IFhirSourceClientFactory>();
        factory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);
        return (client, factory);
    }

    private static async Task<WorkflowNodeOutput> RunAsync(
        Guid sourceConnectionId,
        FhirSourceConfiguration source,
        Mock<IFhirSourceClient> client,
        Mock<IFhirSourceClientFactory> clientFactory,
        string resources,
        string destResources)
    {
        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var workflowStore = new InMemoryWorkflowDefinitionStore(TestHelpers.LicenseTestScopeFactory.Create());
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "reference-scoped-fetch-test", 1);
        var sourceNode = workflow.AddNode(
            WorkflowNodeTypes.EpicSource,
            WorkflowNodeCategory.Source,
            rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{sourceConnectionId}}","Resources":"{{resources}}"}""");
        var destinationNode = workflow.AddNode(
            WorkflowNodeTypes.FhirRepositoryDestination,
            WorkflowNodeCategory.Destination,
            rank: 1,
            configurationJson: $$"""{"dest_resources":"{{destResources}}"}""");
        workflow.AddEdge(sourceNode.Id, destinationNode.Id);
        await workflowStore.SaveAsync(workflow, CancellationToken.None);

        var executor = new EpicSourceNodeExecutor(
            clientFactory.Object, resolver.Object, workflowDefinitionStore: workflowStore);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        return await executor.ExecuteAsync(context, sourceNode, [], CancellationToken.None);
    }

    [Fact]
    public async Task Practitioner_referenced_by_a_sibling_is_read_by_id_instead_of_searched()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId);

        var (client, clientFactory) = BuildClientFactory();
        var encounterJson = """{"resourceType":"Encounter","id":"e1","participant":[{"individual":{"reference":"Practitioner/pr1"}}]}""";
        client
            .Setup(x => x.SearchAsync("Patient", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Patient", "p1", "{}", null, null)]);
        client
            .Setup(x => x.SearchAsync("Encounter", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Encounter", "e1", encounterJson, null, null)]);
        client
            .Setup(x => x.ReadByIdAsync("Practitioner", "pr1", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceEnvelope("Practitioner", "pr1", """{"resourceType":"Practitioner","id":"pr1"}""", null, null));

        var output = await RunAsync(sourceConnectionId, source, client, clientFactory, "Patient, Encounter, Practitioner", "Patient,Encounter,Practitioner");

        client.Verify(x => x.SearchAsync("Practitioner", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(x => x.ReadByIdAsync("Practitioner", "pr1", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()), Times.Once);

        var resources = output.Payload.Should().BeOfType<ResourceBatch>().Subject.Resources;
        resources.Should().Contain(r => r.ResourceType == "Practitioner" && r.ResourceId == "pr1");
    }

    [Fact]
    public async Task Organization_and_medication_referenced_across_multiple_siblings_are_deduped_and_read_by_id()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId);

        var (client, clientFactory) = BuildClientFactory();
        var patientJson = """{"resourceType":"Patient","id":"p1","managingOrganization":{"reference":"Organization/org1"}}""";
        var encounterJson = """{"resourceType":"Encounter","id":"e1","serviceProvider":{"reference":"Organization/org1"}}""";
        var medRequestJson = """{"resourceType":"MedicationRequest","id":"mr1","medicationReference":{"reference":"Medication/med1"}}""";

        client
            .Setup(x => x.SearchAsync("Patient", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Patient", "p1", patientJson, null, null)]);
        client
            .Setup(x => x.SearchAsync("Encounter", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Encounter", "e1", encounterJson, null, null)]);
        client
            .Setup(x => x.SearchAsync("MedicationRequest", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("MedicationRequest", "mr1", medRequestJson, null, null)]);
        client
            .Setup(x => x.ReadByIdAsync("Organization", "org1", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceEnvelope("Organization", "org1", """{"resourceType":"Organization","id":"org1"}""", null, null));
        client
            .Setup(x => x.ReadByIdAsync("Medication", "med1", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceEnvelope("Medication", "med1", """{"resourceType":"Medication","id":"med1"}""", null, null));

        var output = await RunAsync(
            sourceConnectionId, source, client, clientFactory,
            "Patient, Encounter, MedicationRequest, Organization, Medication",
            "Patient,Encounter,MedicationRequest,Organization,Medication");

        // org1 is referenced by both Patient and Encounter — must be read exactly once, not twice.
        client.Verify(x => x.ReadByIdAsync("Organization", "org1", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(x => x.ReadByIdAsync("Medication", "med1", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(x => x.SearchAsync("Organization", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(x => x.SearchAsync("Medication", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);

        var resources = output.Payload.Should().BeOfType<ResourceBatch>().Subject.Resources;
        resources.Should().Contain(r => r.ResourceType == "Organization" && r.ResourceId == "org1");
        resources.Should().Contain(r => r.ResourceType == "Medication" && r.ResourceId == "med1");
    }

    [Fact]
    public async Task Practitioner_selected_alone_with_no_referencing_sibling_falls_back_to_the_pre_existing_unscoped_search()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId);

        var (client, clientFactory) = BuildClientFactory();
        client
            .Setup(x => x.SearchAsync("Practitioner", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Practitioner", "pr1", """{"resourceType":"Practitioner","id":"pr1"}""", null, null)]);

        var output = await RunAsync(sourceConnectionId, source, client, clientFactory, "Practitioner", "Practitioner");

        client.Verify(x => x.SearchAsync("Practitioner", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(x => x.ReadByIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);

        var resources = output.Payload.Should().BeOfType<ResourceBatch>().Subject.Resources;
        resources.Should().Contain(r => r.ResourceType == "Practitioner" && r.ResourceId == "pr1");
    }

    [Fact]
    public async Task Location_referenced_by_an_encounter_is_read_by_id_instead_of_searched()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId);

        var (client, clientFactory) = BuildClientFactory();
        var encounterJson = """{"resourceType":"Encounter","id":"e1","location":[{"location":{"reference":"Location/loc1"}}]}""";
        client
            .Setup(x => x.SearchAsync("Encounter", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Encounter", "e1", encounterJson, null, null)]);
        client
            .Setup(x => x.ReadByIdAsync("Location", "loc1", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceEnvelope("Location", "loc1", """{"resourceType":"Location","id":"loc1"}""", null, null));

        var output = await RunAsync(sourceConnectionId, source, client, clientFactory, "Encounter, Location", "Encounter,Location");

        client.Verify(x => x.SearchAsync("Location", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(x => x.ReadByIdAsync("Location", "loc1", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()), Times.Once);

        var resources = output.Payload.Should().BeOfType<ResourceBatch>().Subject.Resources;
        resources.Should().Contain(r => r.ResourceType == "Location" && r.ResourceId == "loc1");
    }

    [Fact]
    public async Task Specimen_referenced_by_an_observation_is_read_by_id_instead_of_searched()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId);

        var (client, clientFactory) = BuildClientFactory();
        var observationJson = """{"resourceType":"Observation","id":"o1","specimen":{"reference":"Specimen/spec1"}}""";
        client
            .Setup(x => x.SearchAsync("Observation", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Observation", "o1", observationJson, null, null)]);
        client
            .Setup(x => x.ReadByIdAsync("Specimen", "spec1", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceEnvelope("Specimen", "spec1", """{"resourceType":"Specimen","id":"spec1"}""", null, null));

        var output = await RunAsync(sourceConnectionId, source, client, clientFactory, "Observation, Specimen", "Observation,Specimen");

        client.Verify(x => x.SearchAsync("Specimen", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(x => x.ReadByIdAsync("Specimen", "spec1", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()), Times.Once);

        var resources = output.Payload.Should().BeOfType<ResourceBatch>().Subject.Resources;
        resources.Should().Contain(r => r.ResourceType == "Specimen" && r.ResourceId == "spec1");
    }

    [Fact]
    public async Task One_failed_referenced_id_read_does_not_drop_the_others()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId);

        var (client, clientFactory) = BuildClientFactory();
        var encounterJson = """{"resourceType":"Encounter","id":"e1","participant":[{"individual":{"reference":"Practitioner/pr-missing"}},{"individual":{"reference":"Practitioner/pr-ok"}}]}""";
        client
            .Setup(x => x.SearchAsync("Encounter", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Encounter", "e1", encounterJson, null, null)]);
        client
            .Setup(x => x.ReadByIdAsync("Practitioner", "pr-missing", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Epic FHIR request returned 403 (Forbidden)"));
        client
            .Setup(x => x.ReadByIdAsync("Practitioner", "pr-ok", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceEnvelope("Practitioner", "pr-ok", """{"resourceType":"Practitioner","id":"pr-ok"}""", null, null));

        var output = await RunAsync(sourceConnectionId, source, client, clientFactory, "Encounter, Practitioner", "Encounter,Practitioner");

        var resources = output.Payload.Should().BeOfType<ResourceBatch>().Subject.Resources;
        resources.Should().Contain(r => r.ResourceType == "Practitioner" && r.ResourceId == "pr-ok");
        resources.Should().NotContain(r => r.ResourceType == "Practitioner" && r.ResourceId == "pr-missing");
    }
}
