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
        workflow.AddNode(
            "AthenahealthSourceNode", WorkflowNodeCategory.Source, rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{sourceConnectionId}}"}""");
        workflow.AddNode(
            "FhirRepositoryDestinationNode", WorkflowNodeCategory.Destination, rank: 30,
            configurationJson: $$"""{"dest_resources":"{{destResources}}","dest_autoFetchMissingReferences":"true"}""");
        return workflow;
    }

    private static WorkflowDefinition WorkflowWithEpicSourceAndDestination(
        Guid sourceConnectionId, string destResources)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "wf", 1);
        workflow.AddNode(
            "EpicSourceNode", WorkflowNodeCategory.Source, rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{sourceConnectionId}}"}""");
        workflow.AddNode(
            "SqlServerDestinationNode", WorkflowNodeCategory.Destination, rank: 30,
            configurationJson: $$"""{"dest_resources":"{{destResources}}"}""");
        return workflow;
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
}
