using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.Exceptions;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Moq;
using ResourceEnvelope = FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

// Covers the proactive (pre-flight) authorization-session scope check: a resource type this node is configured to
// fetch must actually be covered by the connection's granted SMART scopes before any FHIR request is attempted —
// distinct from SourceNodeExecutorAuthorizationTests, which covers the reactive 401/403-from-the-server path.
public sealed class SourceNodeExecutorScopeAuthorizationTests
{
    [Fact]
    public async Task Resource_type_not_covered_by_granted_scopes_is_skipped_without_ever_calling_the_client()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null,
            ["system/Patient.rs"],
            SourceConnectionId: sourceConnectionId);

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var client = new Mock<IFhirSourceClient>();
        client
            .Setup(x => x.SearchAsync("Patient", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Patient", "p1", "{}", null, null)]);

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object);
        var node = BuildNode(sourceConnectionId, "Patient,Observation");
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var output = await executor.ExecuteAsync(context, node, [], CancellationToken.None);

        output.Payload.Should().BeOfType<ResourceBatch>().Which.Resources.Should().HaveCount(1);
        output.Metadata!["skippedResourceTypes"].Should().BeAssignableTo<string[]>()
            .Which.Should().ContainSingle(reason => reason.Contains("Observation") && reason.Contains("no granted SMART scope"));

        // Never even attempted — the missing scope was caught before any request went out.
        client.Verify(x => x.SearchAsync("Observation", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Parent_type_not_covered_by_granted_scopes_cancels_the_run_without_ever_calling_the_client()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null,
            ["system/Observation.rs"],
            SourceConnectionId: sourceConnectionId);

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var client = new Mock<IFhirSourceClient>();
        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object);
        var node = BuildNode(sourceConnectionId, "Patient,Observation");
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var act = () => executor.ExecuteAsync(context, node, [], CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<WorkflowRunCancelledException>();
        thrown.Which.ResourceType.Should().Be("Patient");

        client.Verify(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Connection_with_no_declared_scopes_skips_the_check_entirely()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId);

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var client = new Mock<IFhirSourceClient>();
        client
            .Setup(x => x.SearchAsync("Observation", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Observation", "o1", "{}", null, null)]);

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object);
        var node = BuildNode(sourceConnectionId, "Observation");
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var output = await executor.ExecuteAsync(context, node, [], CancellationToken.None);

        output.Metadata!["skippedResourceTypes"].Should().BeNull();
        output.Payload.Should().BeOfType<ResourceBatch>().Which.Resources.Should().HaveCount(1);
    }

    // Covers the real-granted-scope diff: the connection's own configured/derived scopes (source.Scopes) say every
    // requested resource type is covered, but the IdP's ACTUAL token-response grant (surfaced via
    // IFhirGrantedScopeProvider) narrowed it — Observation was requested but never actually granted at the token
    // endpoint. This must be caught pre-flight (never calling the client for Observation), the same as the
    // configured-scope check above, but using the real grant instead of the self-derived config.
    [Fact]
    public async Task Resource_type_dropped_by_the_IdPs_actual_token_grant_is_skipped_without_ever_calling_the_client()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null,
            // Configured/derived scopes claim every requested type is covered.
            ["system/Patient.rs", "system/Encounter.rs", "system/Condition.rs", "system/Observation.rs"],
            SourceConnectionId: sourceConnectionId);

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var client = new Mock<IFhirSourceClient>();
        client
            .Setup(x => x.SearchAsync(It.IsIn("Patient", "Encounter", "Condition"), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string type, FhirSourceConfiguration _, CancellationToken _) =>
                (IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope(type, $"{type}-1", "{}", null, null)]);

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        // The IdP's real token-response grant (e.g. Epic's SMART Backend Services token) only ever covered
        // Patient/Encounter/Condition — Observation was requested but silently dropped at authorization time.
        var accessTokenProvider = new Mock<IFhirAccessTokenProvider>();
        accessTokenProvider.As<IFhirGrantedScopeProvider>()
            .Setup(x => x.GetGrantedScopeAsync(It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("system/Patient.rs system/Encounter.rs system/Condition.rs");

        var executor = new EpicSourceNodeExecutor(
            clientFactory.Object,
            resolver.Object,
            accessTokenProvider: accessTokenProvider.Object);
        var node = BuildNode(sourceConnectionId, "Patient,Encounter,Condition,Observation");
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var output = await executor.ExecuteAsync(context, node, [], CancellationToken.None);

        output.Metadata!["skippedResourceTypes"].Should().BeAssignableTo<string[]>()
            .Which.Should().ContainSingle(reason => reason.Contains("Observation") && reason.Contains("no granted SMART scope"));
        output.Payload.Should().BeOfType<ResourceBatch>().Which.Resources.Should().HaveCount(3);

        // Never even attempted — the real grant's narrowing was caught before any request went out.
        client.Verify(x => x.SearchAsync("Observation", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static WorkflowNode BuildNode(Guid sourceConnectionId, string resources)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "scope-auth-test", 1);
        var configurationJson = $$"""{"sourceConnectionId":"{{sourceConnectionId}}","Resources":"{{resources}}"}""";
        return workflow.AddNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, rank: 0, configurationJson: configurationJson);
    }
}
