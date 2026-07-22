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

// Covers the multi-resource "Select data groups" fix: when a node's resolved resource types include "Patient"
// alongside others (e.g. Observation, Condition), Patient must be extracted first regardless of its position in the
// configured list, and the patient ids it returns must scope every sibling resource type's search instead of each
// type being fetched fully independently (which previously meant an unscoped, whole-tenant fetch).
public sealed class SourceNodeExecutorCohortTests
{
    [Fact]
    public async Task Patient_is_extracted_first_and_its_ids_scope_sibling_resource_searches()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId);

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var callOrder = new List<string>();
        // Epic (and US Core generally) rejects a clinical-resource search scoped to more than one patient at once —
        // FhirSourceConnectorBase's CohortBatchSize is 1, so each sibling resource type gets one call per cohort
        // member rather than one batched, comma-joined call. Capture every call's PatientIds (not just the last).
        var capturedPatientIdsPerCall = new Dictionary<string, List<IReadOnlyCollection<string>?>>
        {
            ["Observation"] = [],
            ["Condition"] = [],
        };

        var client = new Mock<IFhirSourceClient>();
        client
            .Setup(x => x.SearchAsync("Patient", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callOrder.Add("Patient");
                return Task.FromResult<IReadOnlyList<ResourceEnvelope>>(
                    [new ResourceEnvelope("Patient", "p1", "{}", null, null), new ResourceEnvelope("Patient", "p2", "{}", null, null)]);
            });
        client
            .Setup(x => x.SearchAsync("Observation", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns((string _, FhirSourceConfiguration cfg, CancellationToken _) =>
            {
                callOrder.Add("Observation");
                capturedPatientIdsPerCall["Observation"].Add(cfg.PatientIds);
                return Task.FromResult<IReadOnlyList<ResourceEnvelope>>([new ResourceEnvelope("Observation", "o1", "{}", null, null)]);
            });
        client
            .Setup(x => x.SearchAsync("Condition", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns((string _, FhirSourceConfiguration cfg, CancellationToken _) =>
            {
                callOrder.Add("Condition");
                capturedPatientIdsPerCall["Condition"].Add(cfg.PatientIds);
                return Task.FromResult<IReadOnlyList<ResourceEnvelope>>([new ResourceEnvelope("Condition", "c1", "{}", null, null)]);
            });

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object);
        var node = BuildNode(sourceConnectionId, "Observation,Patient,Condition");
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var output = await executor.ExecuteAsync(context, node, [], CancellationToken.None);

        callOrder.Should().StartWith("Patient");
        capturedPatientIdsPerCall["Observation"].Should().HaveCount(2);
        capturedPatientIdsPerCall["Observation"].SelectMany(ids => ids!).Should().BeEquivalentTo(["p1", "p2"]);
        capturedPatientIdsPerCall["Condition"].Should().HaveCount(2);
        capturedPatientIdsPerCall["Condition"].SelectMany(ids => ids!).Should().BeEquivalentTo(["p1", "p2"]);
        // 2 Patient + 2 Observation (one per cohort member) + 2 Condition (one per cohort member).
        output.Payload.Should().BeOfType<ResourceBatch>().Which.Resources.Should().HaveCount(6);
        output.Metadata!["cohortSize"].Should().Be(2);
    }

    [Fact]
    public async Task No_patient_resource_type_leaves_siblings_unscoped()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId);

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        IReadOnlyCollection<string>? capturedPatientIds = null;
        var client = new Mock<IFhirSourceClient>();
        client
            .Setup(x => x.SearchAsync("Observation", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns((string _, FhirSourceConfiguration cfg, CancellationToken _) =>
            {
                capturedPatientIds = cfg.PatientIds;
                return Task.FromResult<IReadOnlyList<ResourceEnvelope>>([new ResourceEnvelope("Observation", "o1", "{}", null, null)]);
            });

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object);
        var node = BuildNode(sourceConnectionId, "Observation");
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var output = await executor.ExecuteAsync(context, node, [], CancellationToken.None);

        capturedPatientIds.Should().BeNull();
        output.Metadata!["cohortSize"].Should().BeNull();
    }

    [Fact]
    public async Task Cohort_forces_patient_scoped_bulk_export_when_scope_unset()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId,
            RetrievalMethod: "bulk-export");

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        FhirBulkExportRequest? capturedObservationRequest = null;
        var bulkExportClient = new Mock<IFhirBulkExportClient>();
        bulkExportClient
            .Setup(x => x.ExportAsync(It.Is<FhirBulkExportRequest>(r => r.ResourceTypes!.Contains("Patient")), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Patient", "p1", "{}", null, null), new ResourceEnvelope("Patient", "p2", "{}", null, null)]);
        bulkExportClient
            .Setup(x => x.ExportAsync(It.Is<FhirBulkExportRequest>(r => r.ResourceTypes!.Contains("Observation")), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns((FhirBulkExportRequest request, FhirSourceConfiguration _, CancellationToken _) =>
            {
                capturedObservationRequest = request;
                return Task.FromResult<IReadOnlyList<ResourceEnvelope>>([new ResourceEnvelope("Observation", "o1", "{}", null, null)]);
            });

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(new Mock<IFhirSourceClient>().Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object, null, bulkExportClient.Object);
        var node = BuildNode(sourceConnectionId, "Patient,Observation");
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        await executor.ExecuteAsync(context, node, [], CancellationToken.None);

        capturedObservationRequest.Should().NotBeNull();
        capturedObservationRequest!.Scope.Should().Be(BulkExportScope.Patient);
        capturedObservationRequest.PatientIds.Should().BeEquivalentTo(["p1", "p2"]);
    }

    [Fact]
    public async Task System_scoped_bulk_export_is_left_unscoped()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId,
            RetrievalMethod: "bulk-export",
            ExportScope: "system");

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        FhirBulkExportRequest? capturedObservationRequest = null;
        var bulkExportClient = new Mock<IFhirBulkExportClient>();
        bulkExportClient
            .Setup(x => x.ExportAsync(It.Is<FhirBulkExportRequest>(r => r.ResourceTypes!.Contains("Patient")), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Patient", "p1", "{}", null, null)]);
        bulkExportClient
            .Setup(x => x.ExportAsync(It.Is<FhirBulkExportRequest>(r => r.ResourceTypes!.Contains("Observation")), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns((FhirBulkExportRequest request, FhirSourceConfiguration _, CancellationToken _) =>
            {
                capturedObservationRequest = request;
                return Task.FromResult<IReadOnlyList<ResourceEnvelope>>([new ResourceEnvelope("Observation", "o1", "{}", null, null)]);
            });

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(new Mock<IFhirSourceClient>().Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object, null, bulkExportClient.Object);
        var node = BuildNode(sourceConnectionId, "Patient,Observation");
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        await executor.ExecuteAsync(context, node, [], CancellationToken.None);

        capturedObservationRequest.Should().NotBeNull();
        capturedObservationRequest!.Scope.Should().Be(BulkExportScope.System);
        capturedObservationRequest.PatientIds.Should().BeNull();
    }

    private static WorkflowNode BuildNode(Guid sourceConnectionId, string resources)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "cohort-test", 1);
        var configurationJson = $$"""{"sourceConnectionId":"{{sourceConnectionId}}","Resources":"{{resources}}"}""";
        return workflow.AddNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, rank: 0, configurationJson: configurationJson);
    }
}
