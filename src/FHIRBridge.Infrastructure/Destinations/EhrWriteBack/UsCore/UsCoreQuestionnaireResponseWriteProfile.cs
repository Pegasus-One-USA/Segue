using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;
using static FHIRBridge.Infrastructure.Destinations.EhrWriteBack.EhrFhirJson;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack.UsCore;

/// <summary>
/// QuestionnaireResponse create, shaped to US Core: the one write athenahealth's and eClinicalWorks' certified FHIR
/// servers both declare. Only a finished response is sent (completed or amended). It must name the questionnaire it
/// answers and its patient. The answers are copied as given (linkId, text, answer value, nested items); every other
/// reference (author, source, encounter, basedOn, partOf) points at the source system and is dropped.
///
/// <para>The questionnaire must be one the EHR knows: a response to a questionnaire defined only in the source is
/// refused by the EHR, which the writer records as rejected. The canonical is sent as given.</para>
/// </summary>
public abstract class UsCoreQuestionnaireResponseWriteProfile : IEhrWriteProfile
{
    private static readonly HashSet<string> SentStatuses = new(StringComparer.Ordinal) { "completed", "amended" };

    private const int MaxItemDepth = 10;

    public abstract SourceSystemType Vendor { get; }

    public string ResourceType => "QuestionnaireResponse";

    public EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        var status = String(source, "status");
        if (status is null || !SentStatuses.Contains(status))
        {
            return EhrShapeResult.Skip(status == "entered-in-error" ? "entered-in-error" : "not-completed");
        }

        var questionnaire = String(source, "questionnaire");
        if (string.IsNullOrWhiteSpace(questionnaire))
        {
            return EhrShapeResult.Reject("missing-questionnaire");
        }

        var subject = Reference(source, "subject");
        if (EhrReferenceResolver.PatientId(subject) is null)
        {
            return EhrShapeResult.Reject("missing-patient");
        }

        var items = CopyItems(Array(source, "item"), 0);
        if (items is null || items.Count == 0)
        {
            return EhrShapeResult.Reject("missing-answers");
        }

        var shaped = new JsonObject
        {
            ["resourceType"] = ResourceType,
            ["questionnaire"] = questionnaire,
            ["status"] = status,
            ["subject"] = new JsonObject { ["reference"] = subject },
        };
        CopyIfString(source, shaped, "authored");
        shaped["item"] = items;
        return EhrShapeResult.Shaped(shaped, subject);
    }

    public void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId) =>
        shaped["subject"] = ReferenceTo("Patient", targetPatientId);

    private static JsonArray? CopyItems(JsonArray? items, int depth)
    {
        if (items is null || depth > MaxItemDepth)
        {
            return null;
        }

        var copied = new JsonArray();
        foreach (var item in items.OfType<JsonObject>())
        {
            var linkId = String(item, "linkId");
            if (string.IsNullOrWhiteSpace(linkId))
            {
                continue;
            }

            var copy = new JsonObject { ["linkId"] = linkId };
            CopyIfString(item, copy, "text");

            var answers = new JsonArray();
            foreach (var answer in Objects(item, "answer"))
            {
                var answerCopy = new JsonObject();
                foreach (var (key, value) in answer)
                {
                    // value[x] only: valueString, valueCoding, valueDate, valueInteger, ... A valueReference points
                    // at the source system, so it is not sent.
                    if (key.StartsWith("value", StringComparison.Ordinal) && key != "valueReference" && value is not null)
                    {
                        answerCopy[key] = value.DeepClone();
                    }
                }

                if (CopyItems(Array(answer, "item"), depth + 1) is { Count: > 0 } nestedInAnswer)
                {
                    answerCopy["item"] = nestedInAnswer;
                }

                if (answerCopy.Count > 0)
                {
                    answers.Add(answerCopy);
                }
            }

            if (answers.Count > 0)
            {
                copy["answer"] = answers;
            }

            if (CopyItems(Array(item, "item"), depth + 1) is { Count: > 0 } nested)
            {
                copy["item"] = nested;
            }

            if (copy.Count > 1 || copy.ContainsKey("answer"))
            {
                copied.Add(copy);
            }
        }

        return copied;
    }
}

/// <summary>athenahealth QuestionnaireResponse create (certified FHIR R4).</summary>
public sealed class AthenahealthQuestionnaireResponseWriteProfile : UsCoreQuestionnaireResponseWriteProfile
{
    public override SourceSystemType Vendor => SourceSystemType.Athenahealth;
}

/// <summary>eClinicalWorks QuestionnaireResponse create (certified FHIR R4).</summary>
public sealed class HealowQuestionnaireResponseWriteProfile : UsCoreQuestionnaireResponseWriteProfile
{
    public override SourceSystemType Vendor => SourceSystemType.Healow;
}
