using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.Fhir;
using static FHIRBridge.Infrastructure.Destinations.EhrWriteBack.EhrFhirJson;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack.GenericFhir;

/// <summary>
/// A plain FHIR R4 create on a Generic FHIR server: the source resource as it is, minus what belongs to the source
/// server. One instance per type in <see cref="EhrWriteCapabilities.GenericFhirResourceTypes"/>.
///
/// <list type="bullet">
/// <item><c>id</c>, <c>meta</c> (except declared profiles) and narrative <c>text</c> go: the server assigns its own,
/// and a narrative may describe what the shaping removed.</item>
/// <item>The patient reference is bound to the server's patient by the writer; the record's encounter is not (a
/// Generic server's encounters are not resolved), so it is removed.</item>
/// <item>Every other literal reference (a practitioner, an organisation, a medication, a plan) names a SOURCE record
/// that the server does not have; a server that checks references would refuse the whole record, and one that does not
/// would hold a dangling link. Each keeps its <c>display</c> and <c>identifier</c> and loses its <c>reference</c>; one
/// left with nothing gets a display saying it was not transferred. Contained (<c>#</c>) references stay.</item>
/// </list>
/// </summary>
public sealed class GenericFhirWriteProfile : IEhrWriteProfile
{
    // Where each type keeps its patient; every other type uses subject.
    private static readonly IReadOnlyDictionary<string, string> PatientProperties = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AllergyIntolerance"] = "patient",
        ["BodyStructure"] = "patient",
        ["Immunization"] = "patient",
    };

    private readonly string _patientProperty;

    public GenericFhirWriteProfile(string resourceType)
    {
        ResourceType = resourceType;
        _patientProperty = PatientProperties.GetValueOrDefault(resourceType, "subject");
    }

    public SourceSystemType Vendor => SourceSystemType.GenericFhir;

    public string ResourceType { get; }

    public EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        if (String(source, "status") == "entered-in-error"
            || Codes(Object(source, "verificationStatus")).Contains("entered-in-error"))
        {
            return EhrShapeResult.Skip("entered-in-error");
        }

        var shaped = new JsonObject();
        string? patientReference = null;
        string? encounterReference = null;
        foreach (var (name, value) in source)
        {
            if (name is "id" or "text" or "meta" || value is null)
            {
                continue;
            }

            if (ResourceType != "Patient" && name == _patientProperty)
            {
                patientReference = String(value, "reference");
                shaped[name] = value.DeepClone();
                continue;
            }

            if (name == "encounter")
            {
                encounterReference = String(value, "reference");
                continue;
            }

            // A DocumentReference's context holds its encounter as well as its period: the encounter goes.
            var copy = value.DeepClone();
            DetachReferences(copy, isContextOfDocument: name == "context");
            shaped[name] = copy;
        }

        if (Array(Object(source, "meta"), "profile") is { Count: > 0 } profiles)
        {
            shaped["meta"] = new JsonObject { ["profile"] = profiles.DeepClone() };
        }

        shaped["resourceType"] = ResourceType;
        if (ResourceType != "Patient" && patientReference is null)
        {
            return EhrShapeResult.Reject("missing-patient");
        }

        return EhrShapeResult.Shaped(Ordered(shaped), patientReference, encounterReference);
    }

    public void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId)
    {
        if (ResourceType != "Patient")
        {
            shaped[_patientProperty] = ReferenceTo("Patient", targetPatientId);
        }
    }

    /// <summary>resourceType first, the rest in source order, so the content hash is stable.</summary>
    private static JsonObject Ordered(JsonObject shaped)
    {
        var ordered = new JsonObject { ["resourceType"] = shaped["resourceType"]!.DeepClone() };
        foreach (var (name, value) in shaped)
        {
            if (name != "resourceType")
            {
                ordered[name] = value?.DeepClone();
            }
        }

        return ordered;
    }

    private static void DetachReferences(JsonNode? node, bool isContextOfDocument)
    {
        switch (node)
        {
            case JsonObject obj:
                if (isContextOfDocument)
                {
                    obj.Remove("encounter");
                }

                if (obj["reference"] is JsonValue reference && reference.TryGetValue<string>(out var text) && !text.StartsWith('#'))
                {
                    var type = EhrFhirReference.TryParse(text, out var parsedType, out _) ? parsedType : "record";
                    obj.Remove("reference");
                    if (!obj.ContainsKey("display") && !obj.ContainsKey("identifier"))
                    {
                        obj["display"] = $"{type} reference not transferred";
                    }
                }

                foreach (var (_, child) in obj.ToList())
                {
                    DetachReferences(child, isContextOfDocument: false);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    DetachReferences(child, isContextOfDocument: false);
                }

                break;
        }
    }
}
