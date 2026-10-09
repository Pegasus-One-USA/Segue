using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;
using static FHIRBridge.Infrastructure.Destinations.EhrWriteBack.EhrFhirJson;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack.Epic;

/// <summary>
/// Epic Observation.Create (Vital Signs), spec 963: one flowsheet reading against an open encounter. Epic takes one
/// coding (LOINC is enough; the sandbox resolved 29463-7 to its Weight row), <c>status</c> final only, an effective
/// time, blood pressure as systolic/diastolic components, and a note of up to 254 characters. A repeated reading at
/// the same time is refused with 59189, which the writer records as already at target.
///
/// <para>Units are passed through; converting to an organisation's default unit is not built yet, so a reading
/// without a unit is rejected rather than left to Epic's silent default.</para>
/// </summary>
public sealed partial class EpicVitalSignWriteProfile : IEhrWriteProfile
{
    private const string CategorySystem = "http://hl7.org/fhir/observation-category";
    private const string BloodPressurePanel = "85354-9";
    private const string Systolic = "8480-6";
    private const string Diastolic = "8462-4";
    private const int MaxNoteLength = 254;

    // Panels group other readings; the readings themselves are filed, never the panel.
    private static readonly HashSet<string> PanelCodes = new(StringComparer.Ordinal) { "85353-1", "8716-3" };

    public SourceSystemType Vendor => SourceSystemType.Epic;

    public string ResourceType => "Observation";

    public EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        if (!Objects(source, "category").Any(category => Codes(category).Contains("vital-signs")))
        {
            return EhrShapeResult.Skip("not-a-vital-sign");
        }

        var status = String(source, "status");
        if (status is not ("final" or "amended" or "corrected"))
        {
            return EhrShapeResult.Skip(status is "entered-in-error" or "cancelled" ? status : "not-final");
        }

        var subject = Reference(source, "subject");
        if (subject is null)
        {
            return EhrShapeResult.Reject("missing-patient");
        }

        var codeSource = Object(source, "code");
        var loinc = Objects(codeSource, "coding")
            .FirstOrDefault(c => String(c, "system") == LoincSystem && String(c, "code") is { Length: > 0 });
        if (loinc is null)
        {
            return EhrShapeResult.Reject("missing-loinc-code");
        }

        var loincCode = String(loinc, "code")!;
        if (PanelCodes.Contains(loincCode))
        {
            return EhrShapeResult.Skip("vital-signs-panel");
        }

        var effective = String(source, "effectiveDateTime") ?? String(Object(source, "effectivePeriod"), "start");
        if (effective is null)
        {
            return EhrShapeResult.Reject("missing-effective-time");
        }

        if (!DateTimeWithZone().IsMatch(effective))
        {
            return EhrShapeResult.Reject(DateTimeWithoutZone().IsMatch(effective) ? "effective-without-timezone" : "effective-without-time");
        }

        var code = new JsonObject { ["coding"] = new JsonArray(CopyCoding(loinc)) };
        CopyIfString(codeSource, code, "text");

        var shaped = new JsonObject
        {
            ["resourceType"] = ResourceType,
            ["status"] = "final",
            ["category"] = new JsonArray(CodeableConcept(CategorySystem, "vital-signs", "Vital Signs")),
            ["code"] = code,
            ["subject"] = new JsonObject { ["reference"] = subject },
            ["effectiveDateTime"] = effective,
        };

        var valueProblem = loincCode == BloodPressurePanel ? ShapeBloodPressure(source, shaped) : ShapeValue(source, shaped);
        if (valueProblem is not null)
        {
            return EhrShapeResult.Reject(valueProblem);
        }

        if (NoteText(source, MaxNoteLength) is { } note)
        {
            shaped["note"] = SingleNote(note);
        }

        return EhrShapeResult.Shaped(shaped, subject, Reference(source, "encounter"));
    }

    public void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId)
    {
        shaped["subject"] = ReferenceTo("Patient", targetPatientId);
        if (targetEncounterId is not null)
        {
            shaped["encounter"] = ReferenceTo("Encounter", targetEncounterId);
        }
    }

    private static string? ShapeValue(JsonObject source, JsonObject shaped)
    {
        if (Object(source, "valueQuantity") is { } quantity)
        {
            if (quantity["value"] is not JsonValue value || !value.TryGetValue<decimal>(out var number))
            {
                return "missing-value";
            }

            var unit = String(quantity, "unit") ?? String(quantity, "code");
            if (string.IsNullOrWhiteSpace(unit))
            {
                return "missing-unit";
            }

            var shapedQuantity = new JsonObject { ["value"] = number, ["unit"] = unit };
            CopyIfString(quantity, shapedQuantity, "system");
            CopyIfString(quantity, shapedQuantity, "code");
            shaped["valueQuantity"] = shapedQuantity;
            return null;
        }

        if (String(source, "valueString") is { Length: > 0 } text)
        {
            shaped["valueString"] = text;
            return null;
        }

        if (CopyCodedConcept(Object(source, "valueCodeableConcept")) is { } concept)
        {
            shaped["valueCodeableConcept"] = concept;
            return null;
        }

        return "missing-value";
    }

    /// <summary>Epic files blood pressure as its two components and ignores their units (always mm[Hg]).</summary>
    private static string? ShapeBloodPressure(JsonObject source, JsonObject shaped)
    {
        var components = new JsonArray();
        foreach (var part in new[] { Systolic, Diastolic })
        {
            var component = Objects(source, "component").FirstOrDefault(c => HasCoding(Object(c, "code"), LoincSystem, part));
            if (Object(component, "valueQuantity")?["value"] is not JsonValue value || !value.TryGetValue<decimal>(out var number))
            {
                return "blood-pressure-missing-component";
            }

            components.Add(new JsonObject
            {
                ["code"] = CodeableConcept(LoincSystem, part),
                ["valueQuantity"] = new JsonObject { ["value"] = number },
            });
        }

        shaped["component"] = components;
        return null;
    }

    private static JsonObject CopyCoding(JsonObject coding)
    {
        var copy = new JsonObject { ["system"] = LoincSystem, ["code"] = String(coding, "code") };
        CopyIfString(coding, copy, "display");
        return copy;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex DateTimeWithZone();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?$")]
    private static partial Regex DateTimeWithoutZone();
}
