using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Infrastructure.Governance;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace FHIRBridge.UnitTests.Governance;

/// <summary>
/// Provenance carries identifying <c>display</c> text on <c>agent.who</c> and <c>target</c> references — neither
/// is a typical PHI field, so the default Safe Harbor rules need explicit entries for them.
/// </summary>
public sealed class SafeHarborDeIdentificationServiceTests
{
    private static readonly SafeHarborDeIdentificationService Sut = new(new ConfigurationBuilder().Build());

    [Fact]
    public async Task Removes_display_text_from_provenance_agent_and_target()
    {
        const string provenanceJson = """
            {
              "resourceType": "Provenance",
              "id": "prov-1",
              "target": [ { "reference": "Patient/patient-42", "display": "Jane Doe" } ],
              "recorded": "2026-08-18T00:00:00Z",
              "agent": [ { "who": { "reference": "Practitioner/prac-1", "display": "Dr. Jane Smith" } } ]
            }
            """;
        var request = new DeIdentificationRequest("Provenance", "prov-1", provenanceJson, []);

        var result = await Sut.DeIdentifyAsync(request, CancellationToken.None);

        result.Should().NotContain("Jane Doe");
        result.Should().NotContain("Dr. Jane Smith");
        result.Should().Contain("Patient/patient-42");
        result.Should().Contain("Practitioner/prac-1");
    }
}
