using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack.UsCore;
using FluentAssertions;

namespace FHIRBridge.UnitTests.EhrWriteBack;

/// <summary>
/// QuestionnaireResponse create, the one certified FHIR write of athenahealth and eClinicalWorks: a finished response
/// with its questionnaire, patient and answers, and nothing that points back at the source system.
/// </summary>
public sealed class QuestionnaireResponseWriteProfileTests
{
    private static readonly EhrWriteBackRunOptions Options = new(true, false, 500, "preliminary", ["QuestionnaireResponse"]);

    private const string Response = """
        {"resourceType":"QuestionnaireResponse","id":"qr1","status":"completed",
         "questionnaire":"http://example.org/Questionnaire/phq-2",
         "subject":{"reference":"Patient/p1"},"encounter":{"reference":"Encounter/e1"},
         "author":{"reference":"Practitioner/x"},"authored":"2026-10-01T09:00:00Z",
         "item":[
           {"linkId":"1","text":"Little interest?","answer":[{"valueCoding":{"system":"http://loinc.org","code":"LA6568-5"}}]},
           {"linkId":"2","answer":[{"valueReference":{"reference":"Practitioner/x"}}]},
           {"linkId":"3","text":"Group","item":[{"linkId":"3.1","answer":[{"valueInteger":2}]}]}
         ]}
        """;

    private static EhrShapeResult Shape(string json) =>
        new AthenahealthQuestionnaireResponseWriteProfile().Shape(JsonNode.Parse(json)!.AsObject(), Options);

    [Fact]
    public void A_completed_response_keeps_its_answers_and_drops_source_references()
    {
        var result = Shape(Response);

        result.Outcome.Should().Be(EhrShapeOutcome.Shaped);
        result.SourcePatientReference.Should().Be("Patient/p1");
        var shaped = result.Resource!;
        shaped.ContainsKey("encounter").Should().BeFalse();
        shaped.ContainsKey("author").Should().BeFalse();
        shaped.ContainsKey("id").Should().BeFalse();
        var items = shaped["item"]!.AsArray();
        items.Select(i => i!["linkId"]!.GetValue<string>()).Should().Equal(new[] { "1", "3" },
            because: "an item whose only answer is a reference into the source system carries nothing to send");
        items[1]!["item"]![0]!["answer"]![0]!["valueInteger"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public void Binding_points_the_response_at_the_ehrs_patient()
    {
        var profile = new AthenahealthQuestionnaireResponseWriteProfile();
        var shaped = profile.Shape(JsonNode.Parse(Response)!.AsObject(), Options).Resource!;

        profile.BindReferences(shaped, "a-123", null);

        shaped["subject"]!["reference"]!.GetValue<string>().Should().Be("Patient/a-123");
    }

    [Theory]
    [InlineData("\"status\":\"completed\"", "\"status\":\"in-progress\"", EhrShapeOutcome.Skipped, "not-completed")]
    [InlineData("\"questionnaire\":\"http://example.org/Questionnaire/phq-2\",", "", EhrShapeOutcome.Rejected, "missing-questionnaire")]
    [InlineData("\"subject\":{\"reference\":\"Patient/p1\"},", "", EhrShapeOutcome.Rejected, "missing-patient")]
    public void Unfinished_or_incomplete_responses_are_not_sent(string find, string replace, EhrShapeOutcome outcome, string reason)
    {
        var result = Shape(Response.Replace(find, replace));

        result.Outcome.Should().Be(outcome);
        result.Reason.Should().Be(reason);
    }

    [Fact]
    public void Both_vendors_register_their_own_profile()
    {
        var registry = new EhrWriteProfileRegistry([new AthenahealthQuestionnaireResponseWriteProfile(), new HealowQuestionnaireResponseWriteProfile()]);

        registry.Find(FHIRBridge.Domain.Enums.SourceSystemType.Athenahealth, "QuestionnaireResponse").Should().NotBeNull();
        registry.Find(FHIRBridge.Domain.Enums.SourceSystemType.Healow, "QuestionnaireResponse").Should().NotBeNull();
    }
}
