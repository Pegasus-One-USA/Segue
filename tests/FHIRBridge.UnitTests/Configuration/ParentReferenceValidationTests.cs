using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Application.Validation;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.SharedKernel.Enums;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Configuration;

/// <summary>
/// Pins the save-time enforcement that makes the parent-child mapping feature's "prevent saving a
/// workflow with a missing required reference" requirement actually hold - this is the gate a direct
/// API call cannot bypass, independent of whatever the portal UI did or didn't auto-lock.
/// <see cref="IParentReferenceResolver"/> is mocked here (its own resolution/tie-break logic is covered
/// by <c>ParentReferenceResolverTests</c>) so these tests focus purely on
/// <c>ConfigurationService.ApplyResourceMappingsAsync</c>'s use of the resolved field.
/// </summary>
public sealed class ParentReferenceValidationTests
{
    private static readonly FhirElementDto SubjectReferenceField = new(
        Label: "Subject Reference",
        JsonPath: "$.subject.reference",
        FhirPath: "subject.reference",
        Cardinality: "0..1",
        ValueType: "String",
        IsArray: false,
        Arrays: [],
        ReferenceTargetTypes: ["Patient"]);

    private readonly InMemoryConfigurationRepository _repository = new();
    private readonly InMemorySourceCapabilityRepository _capabilityRepository = new();
    private readonly Mock<ISourceCapabilityDiscoveryService> _discovery = new();
    private readonly Mock<IParentReferenceResolver> _resolver = new();
    private readonly ConfigurationService _sut;

    public ParentReferenceValidationTests()
    {
        _sut = new ConfigurationService(
            _repository, _capabilityRepository, _discovery.Object,
            Mock.Of<ISecretWriter>(),
            new FHIRBridge.UnitTests.Security.PassthroughTenantSecretVaultResolver(),
            _resolver.Object,
            new CreateMappingProfileRequestValidator(
                new FHIRBridge.UnitTests.Validation.NoOpDestinationSchemaService(),
                _repository,
                new FHIRBridge.UnitTests.Validation.NoOpEffectiveRuleResolver()),
            new CreateDestinationConfigurationRequestValidator(),
            new FHIRBridge.UnitTests.Security.PassthroughUserDisplayNameResolver(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigurationService>.Instance);
    }

    [Fact]
    public async Task Save_throws_when_the_required_parent_reference_field_is_not_mapped()
    {
        _resolver.Setup(x => x.Resolve("Observation", "Patient", null)).Returns(SubjectReferenceField);

        var source = await AddInteractiveEpicSourceAsync();
        var patient = await AddMappingAsync(source.Id, "Patient");
        var observation = await AddMappingAsync(source.Id, "Observation"); // only "id" is mapped

        var act = () => _sut.AddResourceRouteAsync("Patient", new CreateResourceRouteRequest(
            IngestionMode.ScheduledPull, null, patient.Id, "0 0 * * *", null, IsEnabled: true, Priority: 0,
            ResourceMappings:
            [
                new ResourceRouteMappingRequest(observation.Id, IsEnabled: true, ExecutionOrder: 1,
                    ParentReferences: [new ParentReferenceRequest(patient.Id)])
            ]), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*subject.reference*")
            .WithMessage("*Patient*");
    }

    [Fact]
    public async Task Save_succeeds_when_the_required_parent_reference_field_is_mapped()
    {
        _resolver.Setup(x => x.Resolve("Observation", "Patient", null)).Returns(SubjectReferenceField);

        var source = await AddInteractiveEpicSourceAsync();
        var patient = await AddMappingAsync(source.Id, "Patient");
        var observation = await AddMappingAsync(source.Id, "Observation",
            new MappingFieldDto("subject_ref", "$.subject.reference", MappingValueType.String, false, null, null));

        var route = await _sut.AddResourceRouteAsync("Patient", new CreateResourceRouteRequest(
            IngestionMode.ScheduledPull, null, patient.Id, "0 0 * * *", null, IsEnabled: true, Priority: 0,
            ResourceMappings:
            [
                new ResourceRouteMappingRequest(observation.Id, IsEnabled: true, ExecutionOrder: 1,
                    ParentReferences: [new ParentReferenceRequest(patient.Id)])
            ]), CancellationToken.None);

        route.ResourceMappings.Single(m => m.MappingProfileId == observation.Id)
            .ParentReferences.Should().ContainSingle(p => p.ParentMappingProfileId == patient.Id);
    }

    [Fact]
    public async Task Save_throws_when_the_declared_parent_is_not_part_of_the_route()
    {
        var source = await AddInteractiveEpicSourceAsync();
        var patient = await AddMappingAsync(source.Id, "Patient");
        var observation = await AddMappingAsync(source.Id, "Observation");
        var unrelatedProfileId = Guid.NewGuid();

        var act = () => _sut.AddResourceRouteAsync("Patient", new CreateResourceRouteRequest(
            IngestionMode.ScheduledPull, null, patient.Id, "0 0 * * *", null, IsEnabled: true, Priority: 0,
            ResourceMappings:
            [
                new ResourceRouteMappingRequest(observation.Id, IsEnabled: true, ExecutionOrder: 1,
                    ParentReferences: [new ParentReferenceRequest(unrelatedProfileId)])
            ]), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not part of this route*");
    }

    [Fact]
    public async Task Save_throws_when_the_child_has_no_reference_field_that_can_target_the_parent()
    {
        _resolver.Setup(x => x.Resolve("Practitioner", "Patient", null)).Returns((FhirElementDto?)null);

        var source = await AddInteractiveEpicSourceAsync();
        var patient = await AddMappingAsync(source.Id, "Patient");
        var practitioner = await AddMappingAsync(source.Id, "Practitioner");

        var act = () => _sut.AddResourceRouteAsync("Patient", new CreateResourceRouteRequest(
            IngestionMode.ScheduledPull, null, patient.Id, "0 0 * * *", null, IsEnabled: true, Priority: 0,
            ResourceMappings:
            [
                new ResourceRouteMappingRequest(practitioner.Id, IsEnabled: true, ExecutionOrder: 1,
                    ParentReferences: [new ParentReferenceRequest(patient.Id)])
            ]), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no FHIR reference field*");
    }

    private async Task<SourceConnectionDto> AddInteractiveEpicSourceAsync(string name = "Epic EHR Launch") =>
        await _sut.AddSourceConnectionAsync(new CreateSourceConnectionRequest(
            name, SourceSystemType.Epic, "https://fhir.epic.com/api/FHIR/R4",
            new SourceAuthenticationDto(AuthenticationType.None, "client-1", null, ["user/Patient.read"],
                null, null, null, null, null),
            ApplicationType.EhrLaunch,
            new SourceInteractiveConfigurationDto(["https://app/callback"], null,
                ["https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4"])),
            CancellationToken.None);

    private async Task<MappingProfileDto> AddMappingAsync(
        Guid sourceId, string resourceType, params MappingFieldDto[] extraFields) =>
        await _sut.AddMappingProfileAsync(new CreateMappingProfileRequest(
            $"{resourceType} -> SQL", resourceType, sourceId, Guid.NewGuid(), $"dbo.{resourceType}",
            [
                new MappingFieldDto("id", "$.id", MappingValueType.String, true, null, null),
                .. extraFields,
            ]),
            CancellationToken.None);
}
