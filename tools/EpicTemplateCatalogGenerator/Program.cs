using System.Text.Json;
using System.Text.RegularExpressions;

// Generates src/FHIRBridge.Application/Mapping/Catalog/fhir-r4-catalog.epic.json from the Epic-profile
// template files under src/FHIRBridge.Application/Mapping/Catalog/EpicTemplates — hand-authored example
// payloads whose leaf values are placeholders like "<Code · 1..1 · required>" instead of real data,
// encoding FHIR type + cardinality + required-ness. Unlike tools/MappingMetadataGenerator (which
// introspects the generic Firely R4 model), this walks the literal Epic template JSON, so choice
// elements (deceasedBoolean/deceasedDateTime, …) are already concrete keys — no fan-out logic needed.
//
// Output shape matches fhir-r4-catalog.json's {fhirVersion, resources: {ResourceType: [...]}} exactly,
// plus two additive fields EmbeddedFhirElementCatalog's loader simply ignores today: isRequired (parsed
// straight from the placeholder's "· required" marker) and sourceExtension (traceability for flattened
// extension fields, null for everything else).

// resourceName -> template file path (relative to this tool's own EpicTemplates folder).
(string ResourceName, string FileName)[] targetTemplates =
[
    ("Patient", "patient_template.epic-r4.json"),
    ("AllergyIntolerance", "allergyintolerance_template.epic-r4.json"),
    ("Condition", "condition_template.epic-r4.json"),
    ("DiagnosticReport", "diagnosticreport_template.epic-r4.json"),
    ("Encounter", "encounter_template.epic-r4.json"),
    ("MedicationAdministration", "medicationadministration_template.epic-r4.json"),
    ("MedicationRequest", "medicationrequest_template.epic-r4.json"),
    ("Observation", "observation_template.epic-r4.json"),
    ("Practitioner", "practitioner_template.epic-r4.json"),
    ("Procedure", "procedure_template.epic-r4.json"),
    ("ServiceRequest", "servicerequest_template.epic-r4.json"),
];

var templatesDir = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..",
    "src", "FHIRBridge.Application", "Mapping", "Catalog", "EpicTemplates"));

// Type names are letters, optionally with digits (e.g. "Base64Binary") — [A-Za-z]+ alone missed those.
var placeholderPattern = new Regex(
    @"^<(?<type>[A-Za-z][A-Za-z0-9]*)\s*·\s*(?<card>\d+\.\.(?:\d+|\*))(?:\s*·\s*(?<req>required))?>$",
    RegexOptions.Compiled);

// Mirrors FHIRBridge.Infrastructure.Normalization.Steps.ExtensionFlatteningNormalizationStep.DefaultRules —
// keep these two lists in sync by hand; duplicated rather than referenced so this standalone tool doesn't
// need a project reference into Infrastructure. Only an extension the runtime pipeline itself flattens
// gets a mapping field here — anything else present in a template is reported (HandleRootExtensions),
// not silently exposed, so the mapping page never offers a path with no real data behind it.
List<ExtensionFlatteningRule> flatteningRules =
[
    new("http://hl7.org/fhir/us/core/StructureDefinition/us-core-race", "text", "race", "Race"),
    new("http://hl7.org/fhir/us/core/StructureDefinition/us-core-race", "ombCategory", "raceCategory", "Race Category"),
    new("http://hl7.org/fhir/us/core/StructureDefinition/us-core-ethnicity", "text", "ethnicity", "Ethnicity"),
    new("http://hl7.org/fhir/us/core/StructureDefinition/us-core-birthsex", null, "birthsex", "Birth Sex"),
    new("http://open.epic.com/FHIR/StructureDefinition/extension/legal-sex", null, "legalSex", "Legal Sex"),
    new("http://open.epic.com/FHIR/StructureDefinition/extension/sex-for-clinical-use", null, "sexForClinicalUse", "Sex For Clinical Use"),
    new("http://open.epic.com/FHIR/StructureDefinition/extension/calculated-pronouns-to-use-for-text", null, "pronouns", "Pronouns"),
];

var resources = new Dictionary<string, List<CatalogField>>(StringComparer.Ordinal);

foreach (var (resourceName, fileName) in targetTemplates)
{
    var path = Path.Combine(templatesDir, fileName);
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"WARN: template not found for {resourceName} at {path} — skipped.");
        continue;
    }

    using var document = JsonDocument.Parse(File.ReadAllText(path));
    var fields = new List<CatalogField>();
    Walk(document.RootElement, fhirPath: "", jsonPath: "$", arrayAncestors: [], fields, isRoot: true);
    fields.Sort((a, b) => string.CompareOrdinal(a.FhirPath, b.FhirPath));
    resources[resourceName] = fields;
    Console.WriteLine($"{resourceName}: {fields.Count} fields");
}

var catalog = new
{
    fhirVersion = "epic-r4",
    generatedBy = "tools/EpicTemplateCatalogGenerator (parsed from Epic profile templates)",
    resources = resources.ToDictionary(
        kv => kv.Key,
        kv => kv.Value.Select(f => new
        {
            label = f.Label,
            jsonPath = f.JsonPath,
            fhirPath = f.FhirPath,
            cardinality = f.Cardinality,
            isRequired = f.IsRequired,
            valueType = f.ValueType,
            isArray = f.IsArray,
            arrays = f.Arrays,
            sourceExtension = f.SourceExtension,
        })),
};

var outPath = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..",
    "src", "FHIRBridge.Application", "Mapping", "Catalog", "fhir-r4-catalog.epic.json"));

var json = JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(outPath, json);
Console.WriteLine($"Wrote {json.Length} bytes -> {outPath}");

// Element names that add noise to a mapping picker, or are FHIR's "primitive extension" carriers
// (any "_fieldName" sibling) — skipped at every level. "extension" is handled separately below via
// HandleRootExtensions rather than being walked raw.
void Walk(
    JsonElement obj,
    string fhirPath,
    string jsonPath,
    IReadOnlyList<string> arrayAncestors,
    List<CatalogField> fields,
    bool isRoot = false)
{
    foreach (var prop in obj.EnumerateObject())
    {
        var name = prop.Name;

        if (name.StartsWith('_'))
        {
            continue;
        }

        if (name is "meta" or "text" or "implicitRules" or "language" or "modifierExtension" or "resourceType")
        {
            continue;
        }

        if (name == "extension")
        {
            if (isRoot)
            {
                HandleRootExtensions(prop.Value, fields);
            }

            continue;
        }

        var childFhirPath = fhirPath.Length == 0 ? name : $"{fhirPath}.{name}";
        var value = prop.Value;

        if (value.ValueKind == JsonValueKind.Array)
        {
            using var enumerator = value.EnumerateArray().GetEnumerator();
            if (!enumerator.MoveNext())
            {
                continue;
            }

            var first = enumerator.Current;
            var childJsonPath = $"{jsonPath}.{name}[*]";

            if (first.ValueKind == JsonValueKind.Object)
            {
                var childArrayAncestors = new List<string>(arrayAncestors) { childFhirPath };
                Walk(first, childFhirPath, childJsonPath, childArrayAncestors, fields);
            }
            else if (first.ValueKind == JsonValueKind.String)
            {
                // A repeating primitive (e.g. name.given): isArray=true on the leaf itself, but its own
                // path is NOT added to arrayAncestors — "arrays" only records ancestor *group* paths
                // (matches the same convention already documented in field-mapping-payload.util.ts).
                AddLeaf(fields, childFhirPath, childJsonPath, isArray: true, arrayAncestors, first.GetString()!);
            }

            continue;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            Walk(value, childFhirPath, $"{jsonPath}.{name}", arrayAncestors, fields);
            continue;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            AddLeaf(fields, childFhirPath, $"{jsonPath}.{name}", isArray: false, arrayAncestors, value.GetString()!);
        }
    }
}

void AddLeaf(
    List<CatalogField> fields,
    string fhirPath,
    string jsonPath,
    bool isArray,
    IReadOnlyList<string> arrayAncestors,
    string placeholder)
{
    var match = placeholderPattern.Match(placeholder.Trim());
    if (!match.Success)
    {
        Console.Error.WriteLine($"WARN: unrecognized placeholder '{placeholder}' at {fhirPath} — skipped.");
        return;
    }

    fields.Add(new CatalogField(
        Label: Prettify(fhirPath),
        JsonPath: jsonPath,
        FhirPath: fhirPath,
        Cardinality: match.Groups["card"].Value,
        IsRequired: match.Groups["req"].Success,
        ValueType: ValueTypeFor(match.Groups["type"].Value),
        IsArray: isArray,
        Arrays: arrayAncestors,
        SourceExtension: null));
}

void HandleRootExtensions(JsonElement extensionArray, List<CatalogField> fields)
{
    var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var entry in extensionArray.EnumerateArray())
    {
        if (entry.TryGetProperty("url", out var url) && url.GetString() is { } u)
        {
            seenUrls.Add(u);
        }
    }

    var unmatched = new HashSet<string>(seenUrls, StringComparer.OrdinalIgnoreCase);

    foreach (var rule in flatteningRules)
    {
        if (!seenUrls.Contains(rule.ExtensionUrl))
        {
            continue;
        }

        unmatched.Remove(rule.ExtensionUrl);

        fields.Add(new CatalogField(
            Label: rule.Label,
            JsonPath: $"$.{rule.TargetProperty}",
            FhirPath: rule.TargetProperty,
            Cardinality: "0..1",
            IsRequired: false,
            ValueType: "String",
            IsArray: false,
            Arrays: [],
            SourceExtension: rule.NestedUrl is null ? rule.ExtensionUrl : $"{rule.ExtensionUrl}#{rule.NestedUrl}"));
    }

    foreach (var url in unmatched)
    {
        Console.Error.WriteLine($"WARN: extension '{url}' has no flattening rule yet — not exposed in the catalog.");
    }
}

static string ValueTypeFor(string fhirTypeName) => fhirTypeName.ToLowerInvariant() switch
{
    "boolean" => "Boolean",
    "integer" or "positiveint" or "unsignedint" => "Integer",
    "decimal" => "Decimal",
    "date" => "Date",
    "datetime" or "instant" => "DateTime",
    _ => "String",
};

// "name.given" -> "Name › Given" (matches tools/MappingMetadataGenerator's picker label convention).
static string Prettify(string fhirPath)
{
    var parts = new List<string>();
    foreach (var seg in fhirPath.Split('.'))
    {
        var words = Regex.Replace(seg, "([a-z0-9])([A-Z])", "$1 $2");
        parts.Add(System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(words));
    }

    return string.Join(" › ", parts);
}

internal sealed record CatalogField(
    string Label,
    string JsonPath,
    string FhirPath,
    string Cardinality,
    bool IsRequired,
    string ValueType,
    bool IsArray,
    IReadOnlyList<string> Arrays,
    string? SourceExtension);

internal sealed record ExtensionFlatteningRule(string ExtensionUrl, string? NestedUrl, string TargetProperty, string Label);
