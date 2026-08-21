using FHIRBridge.Application.Abstractions.Normalization;
using FHIRBridge.Infrastructure.Normalization.Steps;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.UnitTests.Normalization;

/// <summary>
/// Provenance has no <c>subject</c>/<c>patient</c> field — it links to a patient only indirectly via
/// <c>target[]</c> references — so it needs its own resolution path distinct from other clinical resources.
/// </summary>
public sealed class PatientMatchingNormalizationStepTests
{
    private static readonly PatientMatchingNormalizationStep Sut = new(
        NullLogger<PatientMatchingNormalizationStep>.Instance);

    [Fact]
    public async Task Resolves_master_patient_id_from_provenance_target_reference()
    {
        const string provenanceJson = """
            {
              "resourceType": "Provenance",
              "id": "prov-1",
              "target": [
                { "reference": "Observation/obs-1" },
                { "reference": "Patient/patient-42", "display": "Jane Doe" }
              ],
              "recorded": "2026-08-18T00:00:00Z"
            }
            """;
        var request = new ResourceNormalizationRequest(Guid.NewGuid(), "Provenance", "prov-1", provenanceJson);
        var current = new ResourceNormalizationResult(provenanceJson, [], []);

        var result = await Sut.ApplyAsync(request, current, CancellationToken.None);

        result.MasterPatientId.Should().NotBeNullOrWhiteSpace();
        result.MasterPatientId.Should().StartWith("mpi-");
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Warns_when_provenance_target_has_no_patient_reference()
    {
        const string provenanceJson = """
            {
              "resourceType": "Provenance",
              "id": "prov-2",
              "target": [
                { "reference": "Observation/obs-1" }
              ],
              "recorded": "2026-08-18T00:00:00Z"
            }
            """;
        var request = new ResourceNormalizationRequest(Guid.NewGuid(), "Provenance", "prov-2", provenanceJson);
        var current = new ResourceNormalizationResult(provenanceJson, [], []);

        var result = await Sut.ApplyAsync(request, current, CancellationToken.None);

        result.MasterPatientId.Should().BeNull();
        result.Warnings.Should().ContainSingle(w => w.Contains("Provenance"));
    }
}
