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

        var bulkExportClient = new Mock<IFhirBulkExportClient>();
        bulkExportClient
            .Setup(x => x.ExportAsync(It.IsAny<FhirBulkExportRequest>(), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)
                [new ResourceEnvelope("Patient", "p1", "{}", null, null), new ResourceEnvelope("Observation", "o1", "{}", null, null)]);

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(new Mock<IFhirSourceClient>().Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object, null, bulkExportClient.Object);
        var node = BuildNode(sourceConnectionId, "Patient,Observation");
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var output = await executor.ExecuteAsync(context, node, [], CancellationToken.None);

        // A System/Group-scoped bulk export must go out as ONE job covering every resource type — a job scoped to
        // just _type=Patient trips Epic Interconnect's Group-export business rule (59159, "requires demographics or
        // _id parameter"), which is exactly the bug this batching fixes. Two separate single-type jobs here would be
        // a regression back to that failure mode.
        bulkExportClient.Verify(
            x => x.ExportAsync(It.IsAny<FhirBulkExportRequest>(), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()),
            Times.Once);

        var capturedRequest = bulkExportClient.Invocations.Single().Arguments[0].Should().BeOfType<FhirBulkExportRequest>().Subject;
        capturedRequest.Scope.Should().Be(BulkExportScope.System);
        capturedRequest.PatientIds.Should().BeNull();
        capturedRequest.ResourceTypes.Should().BeEquivalentTo(["Patient", "Observation"]);

        output.Payload.Should().BeOfType<ResourceBatch>().Which.Resources.Should().HaveCount(2);
    }

    [Fact]
    public async Task Group_scoped_bulk_export_of_only_patient_omits_type_parameter()
    {
        // Epic Interconnect rejects a Group export scoped to ONLY _type=Patient — it internally falls back to an
        // unscoped/demographics Patient search to materialize group membership, which its own business rule then
        // rejects (code 59159, "requires demographics or _id parameter"). A workflow whose source AND destination
        // are both configured for just "Patient" has no other resource type to batch alongside it (see
        // BulkExportScopes.ResolveTypeParameter), so the fix is to omit _type entirely for this lone-Patient case.
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId,
            RetrievalMethod: "bulk-export",
            ExportScope: "group",
            GroupId: "group-123");

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var bulkExportClient = new Mock<IFhirBulkExportClient>();
        bulkExportClient
            .Setup(x => x.ExportAsync(It.IsAny<FhirBulkExportRequest>(), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Patient", "p1", "{}", null, null)]);

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(new Mock<IFhirSourceClient>().Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object, null, bulkExportClient.Object);
        var node = BuildNode(sourceConnectionId, "Patient");
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var output = await executor.ExecuteAsync(context, node, [], CancellationToken.None);

        bulkExportClient.Verify(
            x => x.ExportAsync(It.IsAny<FhirBulkExportRequest>(), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()),
            Times.Once);

        var capturedRequest = bulkExportClient.Invocations.Single().Arguments[0].Should().BeOfType<FhirBulkExportRequest>().Subject;
        capturedRequest.Scope.Should().Be(BulkExportScope.Group);
        capturedRequest.GroupId.Should().Be("group-123");
        capturedRequest.ResourceTypes.Should().BeNull();

        output.Payload.Should().BeOfType<ResourceBatch>().Which.Resources.Should().HaveCount(1);
    }

    [Fact]
    public async Task Node_level_retrieval_config_drives_bulk_export_even_when_connection_entity_has_none()
    {
        // Regression for the real-world failure: the workflow-builder's "Retrieval Configuration" panel (Data
        // Retrieval Method / Export Scope / Group ID / FHIR output format) is workflow-node-scoped — it's written
        // into WorkflowNodes.ConfigurationJson, never synced back onto the reusable SourceConnection entity (see
        // epic-audience-form.component.ts's comment on why: one connection can be referenced by several workflows
        // that each need different retrieval behavior). ResolveAsync only hydrates auth/base-URL/scopes from the
        // entity, so a connection whose Retrieval* columns are still null (never separately configured on the
        // Settings -> Source Connections screen) must not silently make useBulkExport resolve false and fall back
        // to an unscoped search-rest fetch — the node's own config has to win.
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, []
            // RetrievalMethod/ExportScope/GroupId/OutputFormat all default to null here — mirrors the DB row.
            , SourceConnectionId: sourceConnectionId);

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var bulkExportClient = new Mock<IFhirBulkExportClient>();
        bulkExportClient
            .Setup(x => x.ExportAsync(It.IsAny<FhirBulkExportRequest>(), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Patient", "p1", "{}", null, null)]);

        var client = new Mock<IFhirSourceClient>();
        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object, null, bulkExportClient.Object);

        var workflow = new WorkflowDefinition(Guid.NewGuid(), "node-retrieval-override-test", 1);
        var configurationJson = $$"""
            {
              "sourceConnectionId":"{{sourceConnectionId}}",
              "sourceConnectionResolved":"true",
              "Resources":"Patient",
              "Retrieval method key":"bulk-export",
              "Export scope":"group",
              "Group ID":"group-abc",
              "FHIR output format":"ndjson"
            }
            """;
        var node = workflow.AddNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, rank: 0, configurationJson: configurationJson);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var output = await executor.ExecuteAsync(context, node, [], CancellationToken.None);

        // A best-effort Group-membership resolution attempt happens once (to try re-issuing this as a narrower
        // Patient-scoped export — see ResolveGroupPatientIdsAsync) and fails since this mock has no "Group"
        // response configured; the run correctly falls back to the original Group-scoped, ResourceTypes-omitted
        // export below rather than erroring. Search-rest must never have been hit — the run went entirely
        // through bulk export/direct-read, never a paged search.
        client.Verify(x => x.ReadByIdAsync("Group", "group-abc", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);

        bulkExportClient.Verify(
            x => x.ExportAsync(It.IsAny<FhirBulkExportRequest>(), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()),
            Times.Once);

        var capturedRequest = bulkExportClient.Invocations.Single().Arguments[0].Should().BeOfType<FhirBulkExportRequest>().Subject;
        capturedRequest.Scope.Should().Be(BulkExportScope.Group);
        capturedRequest.GroupId.Should().Be("group-abc");
        capturedRequest.ResourceTypes.Should().BeNull(); // lone-Patient omission, see ResolveTypeParameter

        var capturedSource = bulkExportClient.Invocations.Single().Arguments[1].Should().BeOfType<FhirSourceConfiguration>().Subject;
        capturedSource.OutputFormat.Should().Be("application/fhir+ndjson");

        output.Payload.Should().BeOfType<ResourceBatch>().Which.Resources.Should().HaveCount(1);
        output.Metadata!["retrievalMethod"].Should().Be("bulk-export");
    }

    [Fact]
    public async Task Group_scoped_export_of_only_patient_resolves_membership_and_narrows_to_patient_scope()
    {
        // The real fix for the bug the omit-_type workaround above dodges: resolving the Group's membership
        // ourselves (a plain FHIR read, not a bulk job) lets the export be re-issued as Patient-scoped with
        // _type=Patient — Epic never hits its Group-export membership-resolution bug on that path, AND it only
        // has to process the one resource type this workflow actually wants instead of every authorized type.
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId,
            RetrievalMethod: "bulk-export",
            ExportScope: "group",
            GroupId: "group-123");

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var bulkExportClient = new Mock<IFhirBulkExportClient>();
        bulkExportClient
            .Setup(x => x.ExportAsync(It.IsAny<FhirBulkExportRequest>(), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Patient", "p1", "{}", null, null)]);

        var groupRawJson = """
            {
              "resourceType": "Group",
              "id": "group-123",
              "member": [
                { "entity": { "reference": "Patient/p1" } },
                { "entity": { "reference": "Patient/p2" } }
              ]
            }
            """;
        var client = new Mock<IFhirSourceClient>();
        client
            .Setup(x => x.ReadByIdAsync("Group", "group-123", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceEnvelope("Group", "group-123", groupRawJson, null, null));
        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object, null, bulkExportClient.Object);
        var node = BuildNode(sourceConnectionId, "Patient");
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var output = await executor.ExecuteAsync(context, node, [], CancellationToken.None);

        // A direct read, not a search — Group?_id=X can reject an id on Epic that GET Group/X resolves fine.
        client.Verify(x => x.ReadByIdAsync("Group", "group-123", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()), Times.Once);

        bulkExportClient.Verify(
            x => x.ExportAsync(It.IsAny<FhirBulkExportRequest>(), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()),
            Times.Once);

        var capturedRequest = bulkExportClient.Invocations.Single().Arguments[0].Should().BeOfType<FhirBulkExportRequest>().Subject;
        capturedRequest.Scope.Should().Be(BulkExportScope.Patient, "resolving membership lets this become a Patient-scoped export instead of Group");
        capturedRequest.GroupId.Should().BeNull();
        capturedRequest.PatientIds.Should().BeEquivalentTo(["p1", "p2"]);
        capturedRequest.ResourceTypes.Should().BeEquivalentTo(["Patient"], "Patient scope has no reason to omit _type — only lone-Patient Group export does");

        output.Payload.Should().BeOfType<ResourceBatch>().Which.Resources.Should().HaveCount(1);
    }

    [Fact]
    public async Task Group_scoped_export_falls_back_to_a_throwaway_second_type_when_membership_cannot_be_resolved()
    {
        // Some Epic Group export ids aren't GET-able FHIR Group resources at all (404 on a plain read despite
        // being valid for $export) — when ResolveGroupPatientIdsAsync can't get membership, batch Patient
        // alongside one other already-authorized resource type (derived from granted scopes, never read from
        // anywhere) instead of omitting _type entirely. Epic's bug only triggers on a lone Patient type, so this
        // still avoids it. The throwaway type's fetched data must never reach this node's output.
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null,
            ["system/Patient.rs", "system/Practitioner.rs"],
            SourceConnectionId: sourceConnectionId,
            RetrievalMethod: "bulk-export",
            ExportScope: "group",
            GroupId: "group-456");

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var bulkExportClient = new Mock<IFhirBulkExportClient>();
        bulkExportClient
            .Setup(x => x.ExportAsync(It.IsAny<FhirBulkExportRequest>(), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[
                new ResourceEnvelope("Patient", "p1", "{}", null, null),
                new ResourceEnvelope("Practitioner", "pr1", "{}", null, null),
            ]);

        var client = new Mock<IFhirSourceClient>();
        // No ReadByIdAsync setup — mirrors the real 404/no-membership case, so ResolveGroupPatientIdsAsync
        // falls back (its own try/catch swallows the resulting null-Task failure).
        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object, null, bulkExportClient.Object);
        var node = BuildNode(sourceConnectionId, "Patient");
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var output = await executor.ExecuteAsync(context, node, [], CancellationToken.None);

        var capturedRequest = bulkExportClient.Invocations.Single().Arguments[0].Should().BeOfType<FhirBulkExportRequest>().Subject;
        capturedRequest.Scope.Should().Be(BulkExportScope.Group);
        capturedRequest.ResourceTypes.Should().BeEquivalentTo(["Patient", "Practitioner"], "two types avoids Epic's lone-Patient Group-export bug");

        // The throwaway Practitioner data Epic returned must be discarded — never surfaced on this node's output.
        var resources = output.Payload.Should().BeOfType<ResourceBatch>().Subject.Resources;
        resources.Should().ContainSingle();
        resources.Single().ResourceType.Should().Be("Patient");
    }

    private static WorkflowNode BuildNode(Guid sourceConnectionId, string resources)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "cohort-test", 1);
        var configurationJson = $$"""{"sourceConnectionId":"{{sourceConnectionId}}","Resources":"{{resources}}"}""";
        return workflow.AddNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, rank: 0, configurationJson: configurationJson);
    }
}
