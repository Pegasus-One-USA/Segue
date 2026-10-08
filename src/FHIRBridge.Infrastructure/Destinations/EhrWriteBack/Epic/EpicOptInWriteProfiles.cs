using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.Fhir;
using static FHIRBridge.Infrastructure.Destinations.EhrWriteBack.EhrFhirJson;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack.Epic;

// Epic's incoming R4 create APIs beyond the five Phase 1-3 ones. Each is a variant a destination must enable
// (EhrWriteCapability.RequiresVariantOptIn), and each was built from its spec at fhir.epic.com/Specifications/Api?id=<id>
// (read 2026-10-07). None has been sent to Epic yet: they are rehearsed on a Generic FHIR test server first.

/// <summary>Shared rules for the opt-in Epic profiles.</summary>
internal static class EpicOptIn
{
    public const string ObservationCategorySystem = "http://open.epic.com/FHIR/StructureDefinition/observation-category";
    public const string DocumentCategorySystem = "http://open.epic.com/FHIR/StructureDefinition/documentreference-category";

    /// <summary>A reference the record carries to a record that must already exist in the TARGET Epic, as
    /// <c>Type/id</c>, or null when it is missing or of another type. Only a CSV / SQL Table source reaches a profile
    /// that reads one (<see cref="EhrWriteCapability.RequiresTargetReferences"/>).</summary>
    public static JsonObject? TargetReference(JsonNode? reference, params string[] allowedTypes)
    {
        if (!EhrFhirReference.TryParse(String(reference, "reference"), out var type, out var id)
            || !allowedTypes.Contains(type, StringComparer.Ordinal))
        {
            return null;
        }

        var copy = ReferenceTo(type, id);
        CopyIfString(reference, copy, "display");
        return copy;
    }

    /// <summary>Identifiers with a <c>use</c> and a value (Epic's radiotherapy APIs need an official and a usual one,
    /// two or more in all), in source order.</summary>
    public static JsonArray UsedIdentifiers(JsonObject source)
    {
        var identifiers = new JsonArray();
        foreach (var identifier in Objects(source, "identifier"))
        {
            if (String(identifier, "use") is not ("official" or "usual") || String(identifier, "value") is not { Length: > 0 })
            {
                continue;
            }

            var copy = new JsonObject { ["use"] = String(identifier, "use") };
            CopyIfString(identifier, copy, "system");
            copy["value"] = String(identifier, "value");
            identifiers.Add(copy);
        }

        return identifiers;
    }

    /// <summary><c>meta</c> with the source's declared profiles and last-updated time, both of which the radiotherapy
    /// APIs require. The source's time, never "now": the ledger's content hash must not change from run to run.</summary>
    public static (JsonObject? Meta, string? Problem) RadiotherapyMeta(JsonObject source, string requiredProfile)
    {
        var sourceMeta = Object(source, "meta");
        var profiles = (Array(sourceMeta, "profile") ?? new JsonArray())
            .OfType<JsonValue>().Select(v => v.ToString()).Where(p => p.Length > 0).ToList();
        if (requiredProfile.Length > 0 && !profiles.Contains(requiredProfile, StringComparer.Ordinal))
        {
            profiles.Insert(0, requiredProfile);
        }

        if (profiles.Count == 0)
        {
            return (null, "missing-profile");
        }

        if (String(sourceMeta, "lastUpdated") is not { Length: > 0 } lastUpdated)
        {
            return (null, "missing-last-updated");
        }

        return (new JsonObject
        {
            ["lastUpdated"] = lastUpdated,
            ["profile"] = new JsonArray(profiles.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
        }, null);
    }

    /// <summary>The source's extensions with one of <paramref name="urls"/>, minus any that hold a reference: those
    /// point at source records (a dose's target volume) that the target does not have.</summary>
    public static JsonArray? ExtensionsWithoutReferences(JsonObject source, IReadOnlySet<string> urls)
    {
        var kept = new JsonArray();
        foreach (var extension in Objects(source, "extension"))
        {
            if (String(extension, "url") is { } url && urls.Contains(url) && !HoldsReference(extension))
            {
                kept.Add(extension.DeepClone());
            }
        }

        return kept.Count > 0 ? kept : null;
    }

    public static void CopyConcepts(JsonObject source, JsonObject target, string property)
    {
        var concepts = new JsonArray();
        foreach (var concept in Objects(source, property))
        {
            if (CopyCodedConcept(concept) is { } copy)
            {
                concepts.Add(copy);
            }
        }

        if (concepts.Count > 0)
        {
            target[property] = concepts;
        }
    }

    private static bool HoldsReference(JsonNode? node) => node switch
    {
        JsonObject obj => obj.ContainsKey("reference") || obj.Any(p => HoldsReference(p.Value)),
        JsonArray array => array.Any(HoldsReference),
        _ => false,
    };
}

/// <summary>
/// Epic Observation.Create (Lines, Drains, Airways), spec 962: an LDA, or an assessment of one, on an OPEN encounter.
/// Recognised by Epic's <c>LDA</c> category. <c>status</c> final only. The code is a LOINC or CADSR code or an encoded
/// flowsheet id; a flowsheet id's system differs between Epic organisations (and between an organisation's production
/// and test environments), so one from another organisation is refused by Epic. The sequelTo extension names the
/// parent LDA in the SOURCE and is never sent. Notes are cut to 254 characters (Epic August 2025 and later).
/// </summary>
public sealed class EpicLinesDrainsAirwaysWriteProfile : IEhrWriteProfile
{
    private const int MaxNoteLength = 254;
    private static readonly string[] ValueProperties = ["valueQuantity", "valueString", "valueCodeableConcept", "valueTime", "valueDateTime"];

    public SourceSystemType Vendor => SourceSystemType.Epic;

    public string ResourceType => "Observation";

    public string? Variant => EhrWriteVariants.LinesDrainsAirways;

    public EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        if (!Objects(source, "category").Any(category => Codes(category).Any(code => string.Equals(code, "LDA", StringComparison.OrdinalIgnoreCase))))
        {
            return EhrShapeResult.Skip("not-a-line-drain-airway");
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

        if (CopyCodedConcept(Object(source, "code")) is not { } code || !code.ContainsKey("coding"))
        {
            return EhrShapeResult.Reject("missing-code");
        }

        var shaped = new JsonObject
        {
            ["resourceType"] = ResourceType,
            ["status"] = "final",
            ["category"] = new JsonArray(CodeableConcept(EpicOptIn.ObservationCategorySystem, "LDA")),
            ["code"] = code,
            ["subject"] = new JsonObject { ["reference"] = subject },
        };

        if (Object(source, "effectivePeriod") is { } period)
        {
            var copy = new JsonObject();
            CopyIfString(period, copy, "start");
            CopyIfString(period, copy, "end");
            if (copy.Count > 0)
            {
                shaped["effectivePeriod"] = copy;
            }
        }
        else if (String(source, "effectiveDateTime") is { Length: > 0 } effective)
        {
            shaped["effectivePeriod"] = new JsonObject { ["start"] = effective };
        }

        foreach (var property in ValueProperties)
        {
            if (source[property] is { } value)
            {
                shaped[property] = value.DeepClone();
                break;
            }
        }

        var components = new JsonArray();
        foreach (var component in Objects(source, "component"))
        {
            if (CopyCodedConcept(Object(component, "code")) is { } componentCode && Object(component, "valueQuantity") is { } quantity)
            {
                components.Add(new JsonObject { ["code"] = componentCode, ["valueQuantity"] = quantity.DeepClone() });
            }
        }

        if (components.Count > 0)
        {
            shaped["component"] = components;
        }

        if (!shaped.ContainsKey("effectivePeriod") && !ValueProperties.Any(shaped.ContainsKey) && components.Count == 0)
        {
            return EhrShapeResult.Reject("missing-value");
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
}

/// <summary>
/// Epic Observation.Create (DICOM Image Characteristics), spec 11224: the Excessive Radiation (ExRad) finding for a CT
/// study, LOINC 96914-7 with category <c>imaging</c>, <c>status</c> final, an effective time, and <c>focus</c> naming
/// the imaging DiagnosticReport IN THE TARGET Epic. Components: 96912-1 (global noise) and 96913-9 (size-adjusted
/// dose), each with a quantity.
/// </summary>
public sealed class EpicImagingCharacteristicsWriteProfile : IEhrWriteProfile
{
    private const string CtDoseCategory = "96914-7";
    private static readonly HashSet<string> ComponentCodes = new(StringComparer.Ordinal) { "96912-1", "96913-9" };

    public SourceSystemType Vendor => SourceSystemType.Epic;

    public string ResourceType => "Observation";

    public string? Variant => EhrWriteVariants.ImagingCharacteristics;

    public EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        if (!Objects(source, "category").Any(category => Codes(category).Contains("imaging"))
            || !HasCoding(Object(source, "code"), LoincSystem, CtDoseCategory))
        {
            return EhrShapeResult.Skip("not-an-imaging-characteristic");
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

        var focus = new JsonArray();
        foreach (var reference in Objects(source, "focus"))
        {
            if (EpicOptIn.TargetReference(reference, "DiagnosticReport") is { } copy)
            {
                focus.Add(copy);
            }
        }

        if (focus.Count == 0)
        {
            return EhrShapeResult.Reject("missing-imaging-report");
        }

        if (String(source, "effectiveDateTime") is not { Length: > 0 } effective)
        {
            return EhrShapeResult.Reject("missing-effective-time");
        }

        var shaped = new JsonObject
        {
            ["resourceType"] = ResourceType,
            ["status"] = "final",
            ["category"] = new JsonArray(CodeableConcept("http://terminology.hl7.org/CodeSystem/observation-category", "imaging")),
            ["code"] = CodeableConcept(LoincSystem, CtDoseCategory, "CT dose and image quality category"),
            ["subject"] = new JsonObject { ["reference"] = subject },
            ["focus"] = focus,
            ["effectiveDateTime"] = effective,
        };

        if (CopyCodedConcept(Object(source, "valueCodeableConcept")) is { } value)
        {
            shaped["valueCodeableConcept"] = value;
        }

        var components = new JsonArray();
        foreach (var component in Objects(source, "component"))
        {
            var code = Objects(Object(component, "code"), "coding")
                .FirstOrDefault(c => String(c, "system") == LoincSystem && String(c, "code") is { } x && ComponentCodes.Contains(x));
            if (code is not null && Object(component, "valueQuantity") is { } quantity)
            {
                components.Add(new JsonObject
                {
                    ["code"] = CodeableConcept(LoincSystem, String(code, "code")!),
                    ["valueQuantity"] = quantity.DeepClone(),
                });
            }
        }

        if (components.Count > 0)
        {
            shaped["component"] = components;
        }

        return EhrShapeResult.Shaped(shaped, subject);
    }

    public void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId) =>
        shaped["subject"] = ReferenceTo("Patient", targetPatientId);
}

/// <summary>
/// Epic BodyStructure.Create (Radiotherapy Volume), spec 11040: a volume delineated for radiotherapy planning, conforming
/// to CodeX <c>codexrt-radiotherapy-volume</c>. Needs two or more identifiers (a DICOM UID as <c>official</c> with
/// system <c>urn:dicom:uid</c>, a display name as <c>usual</c>), <c>meta.lastUpdated</c> and the patient. Location,
/// qualifiers and morphology are SNOMED concepts, copied as given.
/// </summary>
public sealed class EpicRadiotherapyVolumeWriteProfile : IEhrWriteProfile
{
    public const string VolumeProfile = "http://hl7.org/fhir/us/codex-radiation-therapy/StructureDefinition/codexrt-radiotherapy-volume";

    public SourceSystemType Vendor => SourceSystemType.Epic;

    public string ResourceType => "BodyStructure";

    public string? Variant => EhrWriteVariants.RadiotherapyVolume;

    public EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        var declared = (Array(Object(source, "meta"), "profile") ?? new JsonArray()).OfType<JsonValue>().Any(p => p.ToString().Contains("radiotherapy-volume", StringComparison.Ordinal));
        var dicom = Objects(source, "identifier").Any(i => String(i, "system") == "urn:dicom:uid");
        if (!declared && !dicom)
        {
            return EhrShapeResult.Skip("not-a-radiotherapy-volume");
        }

        if (source["active"] is JsonValue active && active.TryGetValue<bool>(out var isActive) && !isActive)
        {
            return EhrShapeResult.Skip("not-active");
        }

        var patient = Reference(source, "patient");
        if (patient is null)
        {
            return EhrShapeResult.Reject("missing-patient");
        }

        var identifiers = EpicOptIn.UsedIdentifiers(source);
        if (identifiers.Count < 2)
        {
            return EhrShapeResult.Reject("missing-identifiers");
        }

        var (meta, metaProblem) = EpicOptIn.RadiotherapyMeta(source, VolumeProfile);
        if (meta is null)
        {
            return EhrShapeResult.Reject(metaProblem!);
        }

        var shaped = new JsonObject
        {
            ["resourceType"] = ResourceType,
            ["meta"] = meta,
            ["identifier"] = identifiers,
        };
        CopyIfString(source, shaped, "description");
        if (CopyCodedConcept(Object(source, "morphology")) is { } morphology)
        {
            shaped["morphology"] = morphology;
        }

        if (CopyCodedConcept(Object(source, "location")) is { } location)
        {
            shaped["location"] = location;
        }

        EpicOptIn.CopyConcepts(source, shaped, "locationQualifier");
        shaped["patient"] = new JsonObject { ["reference"] = patient };
        return EhrShapeResult.Shaped(shaped, patient);
    }

    public void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId) =>
        shaped["patient"] = ReferenceTo("Patient", targetPatientId);
}

/// <summary>
/// Shared shape of Epic's two External Radiotherapy Summary creates (Procedure 11048, ServiceRequest 11044): category
/// SNOMED 1287742003 (Radiotherapy), one of three course codes, two or more identifiers, declared profiles and
/// <c>meta.lastUpdated</c>. The mCODE extensions are kept unless they hold a reference (the dose-to-volume extensions
/// name SOURCE BodyStructures). Other references (a plan, a cancer Condition, a location, a requester) are not carried:
/// <c>reasonCode</c> and <c>bodySite</c> keep the clinical meaning.
/// </summary>
public abstract class EpicRadiotherapySummaryWriteProfile : IEhrWriteProfile
{
    public const string RadiotherapyCategory = "1287742003";

    private static readonly HashSet<string> CourseCodes = new(StringComparer.Ordinal) { "1217123003", "1222565005", "1255724003" };

    public SourceSystemType Vendor => SourceSystemType.Epic;

    public abstract string ResourceType { get; }

    public string? Variant => EhrWriteVariants.RadiotherapySummary;

    protected abstract IReadOnlySet<string> ExtensionUrls { get; }

    public EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        var code = Objects(Object(source, "code"), "coding")
            .FirstOrDefault(c => String(c, "system") == SnomedSystem && String(c, "code") is { } x && CourseCodes.Contains(x));
        if (!Categories(source).Any(c => HasCoding(c, SnomedSystem, RadiotherapyCategory)) || code is null)
        {
            return EhrShapeResult.Skip("not-a-radiotherapy-summary");
        }

        var status = String(source, "status");
        if (status is "entered-in-error" or "revoked")
        {
            return EhrShapeResult.Skip(status);
        }

        if (string.IsNullOrWhiteSpace(status))
        {
            return EhrShapeResult.Reject("missing-status");
        }

        var subject = Reference(source, "subject");
        if (subject is null)
        {
            return EhrShapeResult.Reject("missing-patient");
        }

        var identifiers = EpicOptIn.UsedIdentifiers(source);
        if (identifiers.Count < 2)
        {
            return EhrShapeResult.Reject("missing-identifiers");
        }

        var (meta, metaProblem) = EpicOptIn.RadiotherapyMeta(source, string.Empty);
        if (meta is null)
        {
            return EhrShapeResult.Reject(metaProblem!);
        }

        var shaped = new JsonObject { ["resourceType"] = ResourceType, ["meta"] = meta };
        if (EpicOptIn.ExtensionsWithoutReferences(source, ExtensionUrls) is { } extensions)
        {
            shaped["extension"] = extensions;
        }

        shaped["identifier"] = identifiers;
        shaped["status"] = status;
        var problem = ShapeSpecific(source, shaped);
        if (problem is not null)
        {
            return EhrShapeResult.Reject(problem);
        }

        var category = CodeableConcept(SnomedSystem, RadiotherapyCategory, "Radiotherapy");
        shaped["category"] = CategoryIsList ? new JsonArray(category) : category;
        shaped["code"] = new JsonObject { ["coding"] = new JsonArray(new JsonObject { ["system"] = SnomedSystem, ["code"] = String(code, "code") }) };
        shaped["subject"] = new JsonObject { ["reference"] = subject };
        ShapeTail(source, shaped);
        EpicOptIn.CopyConcepts(source, shaped, "reasonCode");
        EpicOptIn.CopyConcepts(source, shaped, "bodySite");
        return EhrShapeResult.Shaped(shaped, subject);
    }

    public void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId) =>
        shaped["subject"] = ReferenceTo("Patient", targetPatientId);

    /// <summary>R4 <c>ServiceRequest.category</c> is a list, <c>Procedure.category</c> a single concept.</summary>
    protected virtual bool CategoryIsList => true;

    /// <summary>The category concepts, read either way: a source may send a Procedure's as a list.</summary>
    private static IEnumerable<JsonObject> Categories(JsonObject source) =>
        source["category"] is JsonObject single ? [single] : Objects(source, "category");

    /// <summary>Elements between status and category; returns a rejection reason or null.</summary>
    protected virtual string? ShapeSpecific(JsonObject source, JsonObject shaped) => null;

    /// <summary>Elements after subject.</summary>
    protected abstract void ShapeTail(JsonObject source, JsonObject shaped);
}

/// <summary>Epic Procedure.Create (External Radiotherapy Summary), spec 11048: a delivered course.</summary>
public sealed class EpicRadiotherapySummaryProcedureWriteProfile : EpicRadiotherapySummaryWriteProfile
{
    private static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.Ordinal)
    {
        "http://hl7.org/fhir/us/mcode/StructureDefinition/mcode-procedure-intent",
        "http://hl7.org/fhir/us/mcode/StructureDefinition/mcode-radiotherapy-modality-and-technique",
        "http://hl7.org/fhir/us/mcode/StructureDefinition/mcode-radiotherapy-sessions",
        "http://hl7.org/fhir/us/mcode/StructureDefinition/mcode-radiotherapy-dose-delivered-to-volume",
    };

    public override string ResourceType => "Procedure";

    protected override IReadOnlySet<string> ExtensionUrls => Extensions;

    protected override bool CategoryIsList => false;

    protected override void ShapeTail(JsonObject source, JsonObject shaped)
    {
        if (Object(source, "performedPeriod") is { } period)
        {
            var copy = new JsonObject();
            CopyIfString(period, copy, "start");
            CopyIfString(period, copy, "end");
            if (copy.Count > 0)
            {
                shaped["performedPeriod"] = copy;
            }
        }
    }
}

/// <summary>Epic ServiceRequest.Create (External Radiotherapy Summary), spec 11044: a prescribed course.
/// <c>intent</c> is original-order or filler-order.</summary>
public sealed class EpicRadiotherapySummaryServiceRequestWriteProfile : EpicRadiotherapySummaryWriteProfile
{
    private static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.Ordinal)
    {
        "http://hl7.org/fhir/us/mcode/StructureDefinition/mcode-procedure-intent",
        "http://hl7.org/fhir/us/mcode/StructureDefinition/mcode-radiotherapy-modality-and-technique",
        "http://hl7.org/fhir/us/codex-radiation-therapy/StructureDefinition/codexrt-radiotherapy-dose-prescribed-to-volume",
        "http://hl7.org/fhir/us/codex-radiation-therapy/StructureDefinition/codexrt-radiotherapy-intrafraction-verification",
        "http://hl7.org/fhir/us/codex-radiation-therapy/StructureDefinition/codexrt-radiotherapy-image-guided-radiotherapy-modality",
        "http://hl7.org/fhir/us/codex-radiation-therapy/StructureDefinition/codexrt-radiotherapy-respiratory-motion-management",
        "http://hl7.org/fhir/us/codex-radiation-therapy/StructureDefinition/codexrt-radiotherapy-free-breathing-motion-management",
    };

    public override string ResourceType => "ServiceRequest";

    protected override IReadOnlySet<string> ExtensionUrls => Extensions;

    protected override string? ShapeSpecific(JsonObject source, JsonObject shaped)
    {
        if (String(source, "intent") is not { } intent || intent is not ("original-order" or "filler-order"))
        {
            return "intent-not-supported";
        }

        shaped["intent"] = intent;
        return null;
    }

    protected override void ShapeTail(JsonObject source, JsonObject shaped)
    {
        CopyIfString(source, shaped, "authoredOn");
        if (Object(source, "occurrencePeriod") is { } period)
        {
            var copy = new JsonObject();
            CopyIfString(period, copy, "start");
            CopyIfString(period, copy, "end");
            if (copy.Count > 0)
            {
                shaped["occurrencePeriod"] = copy;
            }
        }
    }
}

/// <summary>
/// Epic DocumentReference.Create (Document Information), spec 10050: the metadata of a scanned document, recognised by
/// Epic's <c>document-information</c> category. Epic: "designed for scanning integrations through the Hyperdrive Scan
/// Acquisition Workflow. It cannot be called outside of this workflow." Needs identifier, type, the patient, the date
/// received, a description and the service period; <c>status</c> is current. Author (the scanning user) is a SOURCE
/// user and is not sent; the custodian is sent by name only. An encounter is needed only for document types Epic files
/// at encounter level, which this build does not know, so none is sent.
/// </summary>
public sealed class EpicDocumentInformationWriteProfile : IEhrWriteProfile
{
    public SourceSystemType Vendor => SourceSystemType.Epic;

    public string ResourceType => "DocumentReference";

    public string? Variant => EhrWriteVariants.DocumentInformation;

    public EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        if (!Objects(source, "category").Any(c => HasCoding(c, EpicOptIn.DocumentCategorySystem, "document-information")))
        {
            return EhrShapeResult.Skip("not-a-scanned-document");
        }

        var status = String(source, "status");
        if (status is "superseded" or "entered-in-error")
        {
            return EhrShapeResult.Skip(status);
        }

        var subject = Reference(source, "subject");
        if (subject is null)
        {
            return EhrShapeResult.Reject("missing-patient");
        }

        var identifiers = new JsonArray();
        foreach (var identifier in Objects(source, "identifier"))
        {
            if (String(identifier, "value") is { Length: > 0 })
            {
                var copy = new JsonObject();
                CopyIfString(identifier, copy, "system");
                CopyIfString(identifier, copy, "value");
                identifiers.Add(copy);
            }
        }

        if (identifiers.Count == 0)
        {
            return EhrShapeResult.Reject("missing-identifier");
        }

        if (CopyCodedConcept(Object(source, "type")) is not { } type)
        {
            return EhrShapeResult.Reject("missing-document-type");
        }

        if (String(source, "date") is not { Length: > 0 } date)
        {
            return EhrShapeResult.Reject("missing-date");
        }

        if (String(source, "description") is not { Length: > 0 } description)
        {
            return EhrShapeResult.Reject("missing-description");
        }

        var period = Object(Object(source, "context"), "period");
        var periodCopy = new JsonObject();
        CopyIfString(period, periodCopy, "start");
        CopyIfString(period, periodCopy, "end");
        if (periodCopy.Count == 0)
        {
            return EhrShapeResult.Reject("missing-service-period");
        }

        var shaped = new JsonObject
        {
            ["resourceType"] = ResourceType,
            ["identifier"] = identifiers,
            ["status"] = "current",
            ["type"] = type,
            ["category"] = new JsonArray(CodeableConcept(EpicOptIn.DocumentCategorySystem, "document-information")),
            ["subject"] = new JsonObject { ["reference"] = subject },
            ["date"] = date,
            ["description"] = description,
        };
        if (String(Object(source, "custodian"), "display") is { Length: > 0 } custodian)
        {
            shaped["custodian"] = new JsonObject { ["display"] = custodian };
        }

        shaped["context"] = new JsonObject { ["period"] = periodCopy };
        return EhrShapeResult.Shaped(shaped, subject);
    }

    public void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId) =>
        shaped["subject"] = ReferenceTo("Patient", targetPatientId);
}

/// <summary>
/// Epic DocumentReference.Create (Non-Patient Document Information), spec 10303: a scanned business document for
/// Epic's Tapestry module, recognised by the <c>nonpatient-document-information</c> category and linked through
/// <c>context.related</c> to an employer group, account, CRM communication, vendor or contract that exists in the
/// TARGET Epic. No patient.
/// </summary>
public sealed class EpicNonPatientDocumentWriteProfile : IEhrWriteProfile
{
    private static readonly string[] RelatedTypes = ["Group", "Account", "Communication", "Organization", "Contract"];

    public SourceSystemType Vendor => SourceSystemType.Epic;

    public string ResourceType => "DocumentReference";

    public string? Variant => EhrWriteVariants.NonPatientDocument;

    public EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        if (!Objects(source, "category").Any(c => HasCoding(c, EpicOptIn.DocumentCategorySystem, "nonpatient-document-information")))
        {
            return EhrShapeResult.Skip("not-a-non-patient-document");
        }

        var status = String(source, "status");
        if (status is "superseded" or "entered-in-error")
        {
            return EhrShapeResult.Skip(status);
        }

        var identifiers = new JsonArray();
        foreach (var identifier in Objects(source, "identifier"))
        {
            if (String(identifier, "value") is { Length: > 0 })
            {
                var copy = new JsonObject();
                CopyIfString(identifier, copy, "system");
                CopyIfString(identifier, copy, "value");
                identifiers.Add(copy);
            }
        }

        if (identifiers.Count == 0)
        {
            return EhrShapeResult.Reject("missing-identifier");
        }

        if (CopyCodedConcept(Object(source, "type")) is not { } type)
        {
            return EhrShapeResult.Reject("missing-document-type");
        }

        var related = new JsonArray();
        foreach (var reference in Objects(Object(source, "context"), "related"))
        {
            if (EpicOptIn.TargetReference(reference, RelatedTypes) is { } copy)
            {
                related.Add(copy);
            }
        }

        if (related.Count == 0)
        {
            return EhrShapeResult.Reject("missing-related-record");
        }

        var shaped = new JsonObject
        {
            ["resourceType"] = ResourceType,
            ["identifier"] = identifiers,
            ["status"] = "current",
            ["type"] = type,
            ["category"] = new JsonArray(CodeableConcept(EpicOptIn.DocumentCategorySystem, "nonpatient-document-information")),
            ["context"] = new JsonObject { ["related"] = related },
        };
        return EhrShapeResult.Shaped(shaped, sourcePatientReference: null);
    }

    public void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId)
    {
        // Not about a patient.
    }
}

/// <summary>
/// Epic Communication.Create (Community Resource Communication), spec 10090: a message on a community-resource
/// referral. <c>status</c> in-progress only; <c>basedOn</c> is the referral's ServiceRequest, <c>sender</c> and
/// <c>recipient</c> the parties, all IN THE TARGET Epic; the patient and an encounter are resolved and bound. The
/// payload is text or an attachment (a PDF), sent inline.
/// </summary>
public sealed class EpicCommunityResourceMessageWriteProfile : IEhrWriteProfile
{
    private static readonly string[] PartyTypes = ["Practitioner", "PractitionerRole", "Organization", "Patient", "RelatedPerson", "CareTeam", "HealthcareService"];

    public SourceSystemType Vendor => SourceSystemType.Epic;

    public string ResourceType => "Communication";

    public string? Variant => EhrWriteVariants.CommunityResourceMessage;

    public EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        var basedOn = Objects(source, "basedOn").Select(r => EpicOptIn.TargetReference(r, "ServiceRequest")).OfType<JsonObject>().FirstOrDefault();
        if (basedOn is null)
        {
            return EhrShapeResult.Skip("not-a-community-resource-message");
        }

        var status = String(source, "status");
        if (status != "in-progress")
        {
            return EhrShapeResult.Skip(status is "entered-in-error" ? status : "not-in-progress");
        }

        var subject = Reference(source, "subject");
        if (subject is null)
        {
            return EhrShapeResult.Reject("missing-patient");
        }

        if (EpicOptIn.TargetReference(Object(source, "sender"), PartyTypes) is not { } sender)
        {
            return EhrShapeResult.Reject("missing-sender");
        }

        var recipients = new JsonArray();
        foreach (var recipient in Objects(source, "recipient"))
        {
            if (EpicOptIn.TargetReference(recipient, PartyTypes) is { } copy)
            {
                recipients.Add(copy);
            }
        }

        if (recipients.Count == 0)
        {
            return EhrShapeResult.Reject("missing-recipient");
        }

        if (String(source, "sent") is not { Length: > 0 } sent)
        {
            return EhrShapeResult.Reject("missing-sent-time");
        }

        var payload = new JsonArray();
        foreach (var item in Objects(source, "payload"))
        {
            if (String(item, "contentString") is { Length: > 0 } text)
            {
                payload.Add(new JsonObject { ["contentString"] = text });
            }
            else if (Object(item, "contentAttachment") is { } attachment && String(attachment, "data") is { Length: > 0 })
            {
                var copy = new JsonObject();
                CopyIfString(attachment, copy, "contentType");
                CopyIfString(attachment, copy, "data");
                CopyIfString(attachment, copy, "title");
                payload.Add(new JsonObject { ["contentAttachment"] = copy });
            }
        }

        if (payload.Count == 0)
        {
            return EhrShapeResult.Reject("missing-payload");
        }

        var shaped = new JsonObject
        {
            ["resourceType"] = ResourceType,
            ["basedOn"] = new JsonArray(basedOn),
        };
        if (Objects(source, "partOf").Select(r => EpicOptIn.TargetReference(r, "Task")).OfType<JsonObject>().FirstOrDefault() is { } partOf)
        {
            shaped["partOf"] = new JsonArray(partOf);
        }

        if (Objects(source, "inResponseTo").Select(r => EpicOptIn.TargetReference(r, "Communication")).OfType<JsonObject>().FirstOrDefault() is { } inResponseTo)
        {
            shaped["inResponseTo"] = new JsonArray(inResponseTo);
        }

        shaped["status"] = "in-progress";
        shaped["subject"] = new JsonObject { ["reference"] = subject };
        shaped["sent"] = sent;
        shaped["recipient"] = recipients;
        shaped["sender"] = sender;
        shaped["payload"] = payload;
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
}

/// <summary>
/// Epic QuestionnaireResponse.Create (Patient-Entered Questionnaires), spec 10023: answers to a patient-entered
/// questionnaire Epic already assigned through an appointment or a questionnaire series. <c>subject</c> is that
/// assignment, by identifier (as CarePlan.Read (Questionnaires Due) returns it), not the patient; <c>questionnaire</c>
/// and each <c>linkId</c> are Epic ids. Status in-progress or completed. Answers are valueDecimal, valueString,
/// valueQuantity, valueDate, valueTime or valueBoolean; an integer is sent as a decimal. A wrong answer is refused with
/// 59159.
/// </summary>
public sealed class EpicPatientEnteredQuestionnaireWriteProfile : IEhrWriteProfile
{
    private static readonly string[] AnswerProperties = ["valueDecimal", "valueString", "valueQuantity", "valueDate", "valueTime", "valueBoolean"];

    public SourceSystemType Vendor => SourceSystemType.Epic;

    public string ResourceType => "QuestionnaireResponse";

    public string? Variant => EhrWriteVariants.PatientEnteredQuestionnaire;

    public EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        var status = String(source, "status");
        if (status is not ("in-progress" or "completed"))
        {
            return EhrShapeResult.Skip(status is "entered-in-error" ? status : "not-in-progress-or-completed");
        }

        var assignment = Object(Object(source, "subject"), "identifier");
        if (String(assignment, "system") is not { Length: > 0 } system || String(assignment, "value") is not { Length: > 0 } value)
        {
            return EhrShapeResult.Reject("missing-questionnaire-assignment");
        }

        if (String(source, "questionnaire") is not { Length: > 0 } questionnaire)
        {
            return EhrShapeResult.Reject("missing-questionnaire");
        }

        var items = new JsonArray();
        foreach (var item in Objects(source, "item"))
        {
            if (String(item, "linkId") is not { Length: > 0 } linkId)
            {
                continue;
            }

            var answers = new JsonArray();
            foreach (var answer in Objects(item, "answer"))
            {
                if (answer["valueInteger"] is JsonValue integer && integer.TryGetValue<long>(out var number))
                {
                    answers.Add(new JsonObject { ["valueDecimal"] = number });
                    continue;
                }

                if (AnswerProperties.FirstOrDefault(answer.ContainsKey) is { } property)
                {
                    answers.Add(new JsonObject { [property] = answer[property]!.DeepClone() });
                }
            }

            if (answers.Count > 0)
            {
                items.Add(new JsonObject { ["linkId"] = linkId, ["answer"] = answers });
            }
        }

        if (items.Count == 0)
        {
            return EhrShapeResult.Reject("missing-answers");
        }

        var shaped = new JsonObject
        {
            ["resourceType"] = ResourceType,
            ["subject"] = new JsonObject { ["identifier"] = new JsonObject { ["system"] = system, ["value"] = value } },
            ["questionnaire"] = questionnaire,
            ["status"] = status,
        };
        CopyIfString(source, shaped, "authored");
        shaped["item"] = items;
        return EhrShapeResult.Shaped(shaped, sourcePatientReference: null);
    }

    public void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId)
    {
        // The subject is the questionnaire's assignment, carried as given.
    }
}
