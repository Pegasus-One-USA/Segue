using FHIRBridge.Application.Abstractions.Normalization;
using FHIRBridge.Infrastructure.Normalization.Steps;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.UnitTests.Normalization;

/// <summary>
/// The payer member id (Section 7 "Insurance / Membership") lives in <c>identifier[]</c> disambiguated only by
/// <c>system</c> — there are ~11 identifiers on a typical Epic Patient and the JSONPath evaluator has no
/// array-filter syntax, so a fixed index would be fragile. This step flattens it into a top-level scalar instead.
/// </summary>
public sealed class IdentifierFlatteningNormalizationStepTests
{
    private const string PatientJson = """
        {
          "resourceType": "Patient",
          "id": "eTjDDWfopD0BnRlyEO2mGZQ3",
          "identifier": [
            { "use": "usual", "type": { "text": "EPIC" }, "system": "urn:oid:1.2.840.114350.1.13.0.1.7.5.737384.0", "value": "E3228" },
            {
              "use": "usual",
              "type": { "coding": [ { "system": "http://terminology.hl7.org/CodeSystem/v2-0203", "code": "MB" } ] },
              "system": "https://open.epic.com/FHIR/StructureDefinition/PayerMemberId",
              "value": "3001487"
            },
            { "use": "usual", "system": "urn:oid:2.16.840.1.113883.4.1", "value": "508-44-9068" }
          ]
        }
        """;

    private static readonly IdentifierFlatteningNormalizationStep Sut = new(
        new ConfigurationBuilder().Build(),
        NullLogger<IdentifierFlatteningNormalizationStep>.Instance);

    [Fact]
    public async Task Flattens_payer_member_id_by_system_not_position()
    {
        var request = new ResourceNormalizationRequest(Guid.NewGuid(), "Patient", "eTjDDWfopD0BnRlyEO2mGZQ3", PatientJson);
        var current = new ResourceNormalizationResult(PatientJson, [], []);

        var result = await Sut.ApplyAsync(request, current, CancellationToken.None);

        using var document = System.Text.Json.JsonDocument.Parse(result.NormalizedJson);
        document.RootElement.GetProperty("memberId").GetString().Should().Be("3001487");
    }
}
