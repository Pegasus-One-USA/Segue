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

/// <summary>
/// Covers the "single-patient" retrieval method (eCW's Backend Single Patient API): the node's Patient ID must
/// reach the connector as a patient cohort, and a run with no patient id anywhere must fail loudly rather than
/// issue an unscoped, tenant-wide search under <c>system/</c> scopes.
/// </summary>
public sealed class SourceNodeExecutorSinglePatientTests
{
    [Fact]
    public async Task Single_patient_node_scopes_the_search_to_its_configured_patient_id()
    {
        var (executor, client) = BuildExecutor(out var sourceConnectionId);
        var node = BuildNode(sourceConnectionId, "Observation", "single-patient", patientId: "pat-42");

        var output = await executor.ExecuteAsync(
            new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        var captured = client.Invocations
            .Single(i => (string)i.Arguments[0] == "Observation")
            .Arguments[1].Should().BeOfType<FhirSourceConfiguration>().Subject;

        captured.RetrievalMethod.Should().Be("single-patient");
        captured.PatientIds.Should().BeEquivalentTo(["pat-42"]);
        output.Payload.Should().BeOfType<ResourceBatch>().Subject.Resources.Should().ContainSingle();
    }

    [Fact]
    public async Task Single_patient_node_without_a_patient_id_fails_instead_of_searching_the_whole_tenant()
    {
        var (executor, client) = BuildExecutor(out var sourceConnectionId);
        var node = BuildNode(sourceConnectionId, "Observation", "single-patient", patientId: null);

        var act = async () => await executor.ExecuteAsync(
            new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Single Patient").And.Contain("Patient ID");

        // The point of the guard: nothing was ever asked of the FHIR server.
        client.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Every_other_retrieval_method_is_unaffected_by_the_guard()
    {
        // A search-rest node with no patient id is a perfectly ordinary configuration (the cohort is derived from
        // the Patient search itself) and must keep running exactly as before.
        var (executor, client) = BuildExecutor(out var sourceConnectionId);
        var node = BuildNode(sourceConnectionId, "Observation", "search-rest", patientId: null);

        var output = await executor.ExecuteAsync(
            new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        client.Invocations.Should().NotBeEmpty();
        output.Payload.Should().BeOfType<ResourceBatch>().Subject.Resources.Should().ContainSingle();
    }

    [Fact]
    public async Task A_node_with_no_retrieval_method_at_all_is_unaffected_by_the_guard()
    {
        var (executor, client) = BuildExecutor(out var sourceConnectionId);
        var node = BuildNode(sourceConnectionId, "Observation", retrievalMethod: null, patientId: null);

        await executor.ExecuteAsync(
            new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        client.Invocations.Should().NotBeEmpty();
    }

    private static (SourceNodeExecutor Executor, Mock<IFhirSourceClient> Client) BuildExecutor(
        out Guid sourceConnectionId)
    {
        sourceConnectionId = Guid.NewGuid();
        var connectionId = sourceConnectionId;

        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Healow, "eCW Sandbox", "https://staging-fhir.example.com", null, "client-1", null, null,
            [], SourceConnectionId: connectionId);

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(
                connectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var client = new Mock<IFhirSourceClient>();
        client
            .Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string resourceType, FhirSourceConfiguration _, CancellationToken _) =>
                (IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope(resourceType, "r1", "{}", null, null)]);

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(It.IsAny<RuntimeSourceType>())).Returns(client.Object);

        return (new EClinicalWorksSourceNodeExecutor(clientFactory.Object, resolver.Object), client);
    }

    private static WorkflowNode BuildNode(
        Guid sourceConnectionId,
        string resources,
        string? retrievalMethod,
        string? patientId)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "single-patient-test", 1);
        var configuration = new List<string>
        {
            $"\"sourceConnectionId\":\"{sourceConnectionId}\"",
            $"\"Resources\":\"{resources}\"",
        };
        if (retrievalMethod is not null)
        {
            configuration.Add($"\"Retrieval method key\":\"{retrievalMethod}\"");
        }

        if (patientId is not null)
        {
            configuration.Add($"\"Patient ID / list\":\"{patientId}\"");
        }

        return workflow.AddNode(
            WorkflowNodeTypes.EClinicalWorksSource,
            WorkflowNodeCategory.Source,
            rank: 0,
            configurationJson: "{" + string.Join(',', configuration) + "}");
    }
}
