using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Governance;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Governance;

/// <summary>
/// Provenance carries identifying <c>display</c> text on <c>agent.who</c> and <c>target</c> references — neither
/// is a typical PHI field, so the default Safe Harbor rules need explicit entries for them. Rules are now
/// profile-scoped: a resource routed through a destination with no profile (or a different profile than the
/// one carrying these rules) must pass through unredacted — that's the whole point of per-destination profiles.
/// </summary>
public sealed class SafeHarborDeIdentificationServiceTests
{
    private static readonly Guid ProfileId = Guid.NewGuid();
    private static readonly Guid OtherProfileId = Guid.NewGuid();

    private static readonly TransformationRule[] ProvenanceRules =
    [
        new(TransformScope.ResourceType, TransformNodeType.HashingMasking, """{"mode":"remove"}""",
            resourceType: "Provenance", sourceField: "agent.who.display",
            executionPhase: TransformExecutionPhase.PreMapping, deIdentificationProfileId: ProfileId),
        new(TransformScope.ResourceType, TransformNodeType.HashingMasking, """{"mode":"remove"}""",
            resourceType: "Provenance", sourceField: "target.display",
            executionPhase: TransformExecutionPhase.PreMapping, deIdentificationProfileId: ProfileId),
    ];

    private static SafeHarborDeIdentificationService BuildSut()
    {
        var repository = new Mock<ITransformationRuleRepository>();
        repository
            .Setup(x => x.GetPreMappingRulesAsync(ProfileId, "Provenance", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProvenanceRules);
        repository
            .Setup(x => x.GetPreMappingRulesAsync(OtherProfileId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        return new SafeHarborDeIdentificationService(repository.Object);
    }

    private const string ProvenanceJson = """
        {
          "resourceType": "Provenance",
          "id": "prov-1",
          "target": [ { "reference": "Patient/patient-42", "display": "Jane Doe" } ],
          "recorded": "2026-08-18T00:00:00Z",
          "agent": [ { "who": { "reference": "Practitioner/prac-1", "display": "Dr. Jane Smith" } } ]
        }
        """;

    [Fact]
    public async Task Removes_display_text_from_provenance_agent_and_target()
    {
        var sut = BuildSut();
        var request = new DeIdentificationRequest("Provenance", "prov-1", ProvenanceJson, [], ProfileId);

        var result = await sut.DeIdentifyAsync(request, CancellationToken.None);

        result.Should().NotContain("Jane Doe");
        result.Should().NotContain("Dr. Jane Smith");
        result.Should().Contain("Patient/patient-42");
        result.Should().Contain("Practitioner/prac-1");
    }

    [Fact]
    public async Task Leaves_resource_unchanged_when_no_profile_is_assigned()
    {
        var sut = BuildSut();
        var request = new DeIdentificationRequest("Provenance", "prov-1", ProvenanceJson, [], ProfileId: null);

        var result = await sut.DeIdentifyAsync(request, CancellationToken.None);

        result.Should().Be(ProvenanceJson);
    }

    [Fact]
    public async Task Leaves_resource_unchanged_when_assigned_a_different_profile()
    {
        var sut = BuildSut();
        var request = new DeIdentificationRequest("Provenance", "prov-1", ProvenanceJson, [], OtherProfileId);

        var result = await sut.DeIdentifyAsync(request, CancellationToken.None);

        result.Should().Contain("Jane Doe");
        result.Should().Contain("Dr. Jane Smith");
    }
}
