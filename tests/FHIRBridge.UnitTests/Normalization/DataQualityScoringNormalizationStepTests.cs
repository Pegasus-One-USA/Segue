using FHIRBridge.Application.Abstractions.Normalization;
using FHIRBridge.Infrastructure.Normalization.Steps;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.UnitTests.Normalization;

public sealed class DataQualityScoringNormalizationStepTests
{
    private static readonly DataQualityScoringNormalizationStep Sut = new(
        NullLogger<DataQualityScoringNormalizationStep>.Instance);

    [Fact]
    public async Task Scores_provenance_with_target_recorded_and_agent_present()
    {
        const string provenanceJson = """
            {
              "resourceType": "Provenance",
              "id": "prov-1",
              "target": [ { "reference": "Patient/patient-42" } ],
              "recorded": "2026-08-18T00:00:00Z",
              "agent": [ { "who": { "reference": "Practitioner/prac-1" } } ]
            }
            """;
        var request = new ResourceNormalizationRequest(Guid.NewGuid(), "Provenance", "prov-1", provenanceJson);
        var current = new ResourceNormalizationResult(provenanceJson, [], []);

        var result = await Sut.ApplyAsync(request, current, CancellationToken.None);

        result.DataQualityScore.Should().Be(1.0);
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Flags_low_quality_provenance_missing_agent()
    {
        const string provenanceJson = """
            {
              "resourceType": "Provenance",
              "id": "prov-2",
              "recorded": "2026-08-18T00:00:00Z"
            }
            """;
        var request = new ResourceNormalizationRequest(Guid.NewGuid(), "Provenance", "prov-2", provenanceJson);
        var current = new ResourceNormalizationResult(provenanceJson, [], []);

        var result = await Sut.ApplyAsync(request, current, CancellationToken.None);

        result.DataQualityScore.Should().BeApproximately(1.0 / 3, 0.001);
        result.Warnings.Should().ContainSingle();
    }
}
