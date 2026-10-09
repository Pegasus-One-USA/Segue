using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;
using static FHIRBridge.Infrastructure.Destinations.EhrWriteBack.EhrFhirJson;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack.Epic;

/// <summary>
/// Epic Patient.Create (Demographics), spec 930, and the demographics sent to <c>Patient/$match</c>. Epic treats every
/// name as official and keeps one family name with up to two given names, files only the home address (which needs a
/// line and a city), takes phone and email telecoms, and refuses <c>deceasedDateTime</c> and <c>link</c> outright.
/// Which identifiers a create requires is the organisation's build: the sandbox refuses a patient without an SSN
/// (59108). Patient.Create is match-or-create, so it is only ever called after <c>$match</c> found no one.
/// </summary>
public sealed partial class EpicPatientWriteProfile : IEhrWriteProfile
{
    // Epic's own FHIR-id identifier systems describe a record in the SOURCE Epic; sending them to another Epic is
    // meaningless at best.
    private static readonly HashSet<string> SourceOnlyIdentifierSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "http://open.epic.com/FHIR/StructureDefinition/patient-fhir-id",
        "http://open.epic.com/FHIR/StructureDefinition/patient-dstu2-fhir-id",
    };

    private static readonly HashSet<string> Genders = new(StringComparer.Ordinal) { "male", "female", "other", "unknown" };

    public SourceSystemType Vendor => SourceSystemType.Epic;

    public string ResourceType => "Patient";

    public EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        if (source["deceasedBoolean"] is JsonValue deceased && deceased.TryGetValue<bool>(out var isDeceased) && isDeceased
            || source.ContainsKey("deceasedDateTime"))
        {
            return EhrShapeResult.Skip("deceased-patient");
        }

        var name = ShapeName(source);
        if (name is null)
        {
            return EhrShapeResult.Reject("missing-name");
        }

        var gender = String(source, "gender");
        if (gender is null || !Genders.Contains(gender))
        {
            return EhrShapeResult.Reject("missing-gender");
        }

        var birthDate = String(source, "birthDate");
        if (birthDate is null || !FullDate().IsMatch(birthDate))
        {
            return EhrShapeResult.Reject("missing-birth-date");
        }

        var shaped = new JsonObject { ["resourceType"] = ResourceType, ["active"] = true };

        var identifiers = new JsonArray();
        foreach (var identifier in Objects(source, "identifier"))
        {
            var system = String(identifier, "system");
            var value = String(identifier, "value");
            if (string.IsNullOrWhiteSpace(system) || string.IsNullOrWhiteSpace(value) || SourceOnlyIdentifierSystems.Contains(system))
            {
                continue;
            }

            var copy = new JsonObject();
            CopyIfString(identifier, copy, "use");
            copy["system"] = system;
            copy["value"] = value;
            identifiers.Add(copy);
        }

        if (identifiers.Count > 0)
        {
            shaped["identifier"] = identifiers;
        }

        shaped["name"] = new JsonArray(name);

        var telecoms = new JsonArray();
        foreach (var telecom in Objects(source, "telecom"))
        {
            var system = String(telecom, "system");
            var value = String(telecom, "value");
            if (system is not ("phone" or "email") || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var copy = new JsonObject { ["system"] = system, ["value"] = value };
            if (String(telecom, "use") is { } use && use is "home" or "mobile" or "work")
            {
                copy["use"] = use;
            }

            telecoms.Add(copy);
        }

        if (telecoms.Count > 0)
        {
            shaped["telecom"] = telecoms;
        }

        shaped["gender"] = gender;
        shaped["birthDate"] = birthDate;

        if (ShapeHomeAddress(source) is { } address)
        {
            shaped["address"] = new JsonArray(address);
        }

        return EhrShapeResult.Shaped(shaped, sourcePatientReference: null);
    }

    public void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId)
    {
        // A patient references nothing the writer needs to rebind.
    }

    /// <summary>One name: the official one, else the first. Family plus one or two given names.</summary>
    private static JsonObject? ShapeName(JsonObject source)
    {
        var names = Objects(source, "name").ToList();
        var name = names.FirstOrDefault(n => String(n, "use") == "official") ?? names.FirstOrDefault();
        var family = String(name, "family");
        var given = Array(name, "given")?.OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out var s) ? s : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Take(2)
            .ToList() ?? [];
        if (string.IsNullOrWhiteSpace(family) || given.Count == 0)
        {
            return null;
        }

        var shaped = new JsonObject { ["use"] = "official", ["family"] = family };
        var givenArray = new JsonArray();
        foreach (var part in given)
        {
            givenArray.Add(part);
        }

        shaped["given"] = givenArray;
        return shaped;
    }

    private static JsonObject? ShapeHomeAddress(JsonObject source)
    {
        var address = Objects(source, "address").FirstOrDefault(a => String(a, "use") is null or "home");
        var city = String(address, "city");
        var lines = Array(address, "line")?.OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out var s) ? s : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList() ?? [];
        if (address is null || string.IsNullOrWhiteSpace(city) || lines.Count == 0)
        {
            return null;
        }

        var lineArray = new JsonArray();
        foreach (var line in lines)
        {
            lineArray.Add(line);
        }

        var shaped = new JsonObject { ["use"] = "home", ["line"] = lineArray, ["city"] = city };
        CopyIfString(address, shaped, "state");
        CopyIfString(address, shaped, "postalCode");
        CopyIfString(address, shaped, "country");
        return shaped;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex FullDate();
}
