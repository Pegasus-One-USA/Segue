using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Moq;
using ResourceEnvelope = FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

// A $export request carries no search criteria, so a bulk-export source node applies its resource types' Criteria
// (and, for Patient, an API run's own criteria) to the exported records instead — see EnforceBulkExportCriteria.
public sealed class SourceNodeExecutorBulkExportCriteriaTests
{
    private const string CanvasNodeId = "n-source-1";

    private static readonly IReadOnlyList<ResourceEnvelope> Patients =
    [
        Patient("p1", "female"),
        Patient("p2", "male"),
        Patient("p3", "female"),
    ];

    [Fact]
    public async Task Exported_patients_are_filtered_by_the_patient_criteria_and_narrow_the_sibling_cohort()
    {
        var (output, observationRequest) = await RunAsync(criteriaRows: [("Patient", "gender=female")]);

        Ids(output, "Patient").Should().Equal("p1", "p3");
        observationRequest!.PatientIds.Should().BeEquivalentTo(["p1", "p3"]);
    }

    [Fact]
    public async Task Api_criteria_win_a_parameter_the_patient_criteria_also_set()
    {
        var (output, _) = await RunAsync(criteriaRows: [("Patient", "gender=female")], apiCriteria: "gender=male");

        Ids(output, "Patient").Should().Equal("p2");
    }

    [Fact]
    public async Task Exported_records_are_kept_when_no_criteria_apply()
    {
        var (output, _) = await RunAsync(criteriaRows: [("Observation", "category=laboratory")]);

        Ids(output, "Patient").Should().Equal("p1", "p2", "p3");
    }

    private static IEnumerable<string> Ids(WorkflowNodeOutput output, string resourceType) =>
        output.Payload.Should().BeOfType<ResourceBatch>().Subject.Resources
            .Where(resource => resource.ResourceType == resourceType)
            .Select(resource => resource.ResourceId);

    private static async Task<(WorkflowNodeOutput Output, FhirBulkExportRequest? ObservationRequest)> RunAsync(
        IReadOnlyList<(string ResourceType, string Criteria)> criteriaRows,
        string? apiCriteria = null)
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.GenericFhir, "HAPI Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId,
            RetrievalMethod: "bulk-export",
            PatientSearchCriteria: apiCriteria);

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        FhirBulkExportRequest? observationRequest = null;
        var bulkExportClient = new Mock<IFhirBulkExportClient>();
        bulkExportClient
            .Setup(x => x.ExportAsync(It.Is<FhirBulkExportRequest>(r => r.ResourceTypes!.Contains("Patient")), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Patients);
        bulkExportClient
            .Setup(x => x.ExportAsync(It.Is<FhirBulkExportRequest>(r => r.ResourceTypes!.Contains("Observation")), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns((FhirBulkExportRequest request, FhirSourceConfiguration _, CancellationToken _) =>
            {
                observationRequest = request;
                return Task.FromResult<IReadOnlyList<ResourceEnvelope>>([]);
            });

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.GenericFhir)).Returns(new Mock<IFhirSourceClient>().Object);

        var workflow = new WorkflowDefinition(Guid.NewGuid(), "bulk-criteria-test", 1);
        var sourceNode = workflow.AddNode(
            WorkflowNodeTypes.GenericFhirSource,
            WorkflowNodeCategory.Source,
            rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{sourceConnectionId}}","Resources":"Patient,Observation","canvasNodeId":"{{CanvasNodeId}}"}""");

        var criteriaRepository = new Mock<IResourceTypeCriteriaRepository>();
        criteriaRepository
            .Setup(x => x.ListForWorkflowAsync(workflow.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(criteriaRows
                .Select(row => new ResourceTypeCriteria(workflow.Id, CanvasNodeId, row.ResourceType, row.Criteria))
                .ToList());

        var executor = new GenericFhirSourceNodeExecutor(
            clientFactory.Object, resolver.Object, null, bulkExportClient.Object,
            resourceTypeCriteriaRepository: criteriaRepository.Object);

        var output = await executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), sourceNode, [], CancellationToken.None);
        return (output, observationRequest);
    }

    private static ResourceEnvelope Patient(string id, string gender) =>
        new("Patient", id, $$"""{"resourceType":"Patient","id":"{{id}}","gender":"{{gender}}"}""", null, null);
}
