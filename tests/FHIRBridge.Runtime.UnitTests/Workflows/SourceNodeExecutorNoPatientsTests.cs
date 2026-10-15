using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Moq;
using ResourceEnvelope = FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

// When a node fetches Patient and finds nobody, its patient-scoped types have no patient to be scoped to: they're
// skipped without a request instead of going out unscoped (which Epic rejects, and a permissive server would answer
// with every patient's data).
public sealed class SourceNodeExecutorNoPatientsTests
{
    [Fact]
    public async Task Patient_scoped_types_are_not_requested_when_the_patient_fetch_finds_nobody()
    {
        var (requested, output) = await RunAsync("Patient, Observation, Condition", patients: []);

        requested.Should().Equal("Patient");
        output.Metadata["resourceTypesNotFetchedNoPatients"].Should().BeEquivalentTo(new[] { "Observation", "Condition" });
        output.Metadata["skippedResourceTypes"].Should().BeNull("finding no patients is not a failure, so the run isn't a PartialSuccess");
    }

    [Fact]
    public async Task A_type_outside_the_patient_compartment_is_still_fetched_when_the_patient_fetch_finds_nobody()
    {
        var (requested, _) = await RunAsync("Patient, Location", patients: []);

        requested.Should().Equal("Patient", "Location");
    }

    [Fact]
    public async Task Patient_scoped_types_are_fetched_when_the_patient_fetch_finds_someone()
    {
        var (requested, output) = await RunAsync("Patient, Observation", patients: [new ResourceEnvelope("Patient", "p1", "{}", null, null)]);

        requested.Should().Equal("Patient", "Observation");
        output.Metadata["resourceTypesNotFetchedNoPatients"].Should().BeNull();
    }

    [Fact]
    public async Task A_run_that_does_not_fetch_patient_is_unchanged()
    {
        var (requested, _) = await RunAsync("Observation", patients: []);

        requested.Should().Equal("Observation");
    }

    private static async Task<(List<string> Requested, WorkflowNodeOutput Output)> RunAsync(
        string resources,
        IReadOnlyList<ResourceEnvelope> patients)
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId);

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var requested = new List<string>();
        var client = new Mock<IFhirSourceClient>();
        client
            .Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns((string type, FhirSourceConfiguration _, CancellationToken _) =>
            {
                requested.Add(type);
                return Task.FromResult(type == "Patient"
                    ? patients
                    : (IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope(type, $"{type}-1", "{}", null, null)]);
            });

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var workflow = new WorkflowDefinition(Guid.NewGuid(), "no-patients-test", 1);
        var sourceNode = workflow.AddNode(
            WorkflowNodeTypes.EpicSource,
            WorkflowNodeCategory.Source,
            rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{sourceConnectionId}}","Resources":"{{resources}}"}""");

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object);
        var output = await executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), sourceNode, [], CancellationToken.None);
        return (requested, output);
    }
}
