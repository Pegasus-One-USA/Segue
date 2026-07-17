using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.DTOs;
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
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var callOrder = new List<string>();
        var capturedPatientIds = new Dictionary<string, IReadOnlyCollection<string>?>();

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
                capturedPatientIds["Observation"] = cfg.PatientIds;
                return Task.FromResult<IReadOnlyList<ResourceEnvelope>>([new ResourceEnvelope("Observation", "o1", "{}", null, null)]);
            });
        client
            .Setup(x => x.SearchAsync("Condition", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns((string _, FhirSourceConfiguration cfg, CancellationToken _) =>
            {
                callOrder.Add("Condition");
                capturedPatientIds["Condition"] = cfg.PatientIds;
                return Task.FromResult<IReadOnlyList<ResourceEnvelope>>([new ResourceEnvelope("Condition", "c1", "{}", null, null)]);
            });

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object);
        var node = BuildNode(sourceConnectionId, "Observation,Patient,Condition");
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var output = await executor.ExecuteAsync(context, node, [], CancellationToken.None);

        callOrder.Should().StartWith("Patient");
        capturedPatientIds["Observation"].Should().BeEquivalentTo(["p1", "p2"]);
        capturedPatientIds["Condition"].Should().BeEquivalentTo(["p1", "p2"]);
        output.Payload.Should().BeOfType<ResourceBatch>().Which.Resources.Should().HaveCount(4);
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
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
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
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
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
    public async Task System_scoped_bulk_export_is_left_unscoped_and_audit_warns()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId,
            RetrievalMethod: "bulk-export",
            ExportScope: "system");

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
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

        var recorded = new List<RecordOperationalAuditLogRequest>();
        var auditService = new Mock<IOperationalAuditService>();
        auditService
            .Setup(x => x.RecordAsync(It.IsAny<RecordOperationalAuditLogRequest>(), It.IsAny<CancellationToken>()))
            .Callback<RecordOperationalAuditLogRequest, CancellationToken>((request, _) => recorded.Add(request))
            .Returns(Task.CompletedTask);

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(new Mock<IFhirSourceClient>().Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object, null, bulkExportClient.Object, auditService.Object);
        var node = BuildNode(sourceConnectionId, "Patient,Observation");
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        await executor.ExecuteAsync(context, node, [], CancellationToken.None);

        capturedObservationRequest.Should().NotBeNull();
        capturedObservationRequest!.Scope.Should().Be(BulkExportScope.System);
        capturedObservationRequest.PatientIds.Should().BeNull();

        recorded.Should().ContainSingle(r => r.Action == "BulkExportNotCohortScoped");
    }

    private static WorkflowNode BuildNode(Guid sourceConnectionId, string resources)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "cohort-test", 1);
        var configurationJson = $$"""{"sourceConnectionId":"{{sourceConnectionId}}","Resources":"{{resources}}"}""";
        return workflow.AddNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, rank: 0, configurationJson: configurationJson);
    }
}
