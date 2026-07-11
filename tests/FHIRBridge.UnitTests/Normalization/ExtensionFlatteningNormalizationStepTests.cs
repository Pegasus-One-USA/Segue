using FHIRBridge.Application.Abstractions.Normalization;
using FHIRBridge.Infrastructure.Normalization.Steps;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.UnitTests.Normalization;

/// <summary>
/// Covers the default flattening rules against a real Epic Patient payload (legal-sex, sex-for-clinical-use and
/// calculated-pronouns are Epic extensions carrying a valueCodeableConcept; us-core-race carries both a "text" and
/// an "ombCategory" nested sub-extension). A prior version of <c>ReadScalar</c> only handled valueCoding-shaped
/// values (a "display"/"code" property directly on the value node) and silently returned null for
/// valueCodeableConcept (whose display lives under coding[0] or text), so these fields never flattened.
/// </summary>
public sealed class ExtensionFlatteningNormalizationStepTests
{
    private const string PatientJson = """
        {
          "resourceType": "Patient",
          "id": "eTjDDWfopD0BnRlyEO2mGZQ3",
          "extension": [
            {
              "valueCodeableConcept": {
                "coding": [ { "system": "urn:oid:1.2.840.114350", "code": "male", "display": "male" } ],
                "text": "Male"
              },
              "url": "http://open.epic.com/FHIR/StructureDefinition/extension/legal-sex"
            },
            {
              "valueCodeableConcept": {
                "coding": [ { "system": "urn:oid:1.2.840.114350", "code": "male", "display": "male" } ]
              },
              "url": "http://open.epic.com/FHIR/StructureDefinition/extension/sex-for-clinical-use"
            },
            {
              "extension": [
                { "valueCoding": { "system": "urn:oid:2.16.840.1.113883.6.238", "code": "2028-9", "display": "Asian" }, "url": "ombCategory" },
                { "valueString": "Other Asian", "url": "text" }
              ],
              "url": "http://hl7.org/fhir/us/core/StructureDefinition/us-core-race"
            },
            {
              "extension": [ { "valueString": "Unknown", "url": "text" } ],
              "url": "http://hl7.org/fhir/us/core/StructureDefinition/us-core-ethnicity"
            },
            {
              "valueCodeableConcept": {
                "coding": [ { "system": "http://loinc.org", "code": "LA29518-0", "display": "he/him/his/his/himself" } ]
              },
              "url": "http://open.epic.com/FHIR/StructureDefinition/extension/calculated-pronouns-to-use-for-text"
            }
          ]
        }
        """;

    private static readonly ExtensionFlatteningNormalizationStep Sut = new(
        new ConfigurationBuilder().Build(),
        NullLogger<ExtensionFlatteningNormalizationStep>.Instance);

    [Theory]
    [InlineData("legalSex", "Male")]
    [InlineData("sexForClinicalUse", "male")]
    [InlineData("pronouns", "he/him/his/his/himself")]
    [InlineData("race", "Other Asian")]
    [InlineData("raceCategory", "Asian")]
    [InlineData("ethnicity", "Unknown")]
    public async Task Flattens_expected_property(string flattenedProperty, string expectedValue)
    {
        var request = new ResourceNormalizationRequest(Guid.NewGuid(), "Patient", "eTjDDWfopD0BnRlyEO2mGZQ3", PatientJson);
        var current = new ResourceNormalizationResult(PatientJson, [], []);

        var result = await Sut.ApplyAsync(request, current, CancellationToken.None);

        using var document = System.Text.Json.JsonDocument.Parse(result.NormalizedJson);
        document.RootElement.GetProperty(flattenedProperty).GetString().Should().Be(expectedValue);
    }
}
