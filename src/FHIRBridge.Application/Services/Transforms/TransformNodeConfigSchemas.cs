using FHIRBridge.Application.DTOs.Transforms;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Transforms;

/// <summary>The System Setting key gating the transformation-rules feature's UI surfaces only — the
/// destination wizard's Rules button and the Settings &gt; Transformation Rules screen (Settings &gt; System
/// Settings &gt; General). Seeded by <c>SystemSettingsSeeder</c> with <see cref="DefaultHidden"/> as the
/// default value, read via <c>ISystemSettingsCache.GetBoolAsync</c> from the API's hidden-check endpoint.
/// Deliberately does NOT gate rule application at runtime — hiding the configuration UI must never silently
/// stop already-configured rules from running (<c>MappingNodeExecutor</c>'s rule-application step ignores this
/// flag entirely), so a workflow's output stays correct regardless of whether an admin has hidden the screen.
/// Ships hidden by default; an admin sets the setting to false to reveal the UI.</summary>
public static class TransformationRulesFeatureFlag
{
    public const string SettingKey = "TransformationRules:Hidden";
    public const bool DefaultHidden = true;
}

/// <summary>
/// Declares exactly which config keys each of the 20 transform nodes reads, what kind of control the UI
/// should render for it, and its default — the single source both the per-field Rules dialog and the
/// Global Rules screen render their config editor from, instead of a raw JSON textarea. Keep this in sync
/// with each node's own <c>config.Get(...)</c> calls in <c>Services/Transforms/Nodes/</c> — this is the
/// contract those calls are honoring.
/// </summary>
public static class TransformNodeConfigSchemas
{
    private static TransformConfigFieldSchema Text(
        string key, string label, string? defaultValue = null, string? placeholder = null, bool advanced = false,
        TransformConfigFieldVisibility? visibleWhen = null) =>
        new(key, label, "text", null, defaultValue, placeholder, advanced, visibleWhen);

    // A number input (rejects non-numeric keystrokes at the UI layer) for the many config keys that are
    // parsed as int/decimal by the node itself (config.GetInt / decimal.TryParse) but used to be a plain
    // text box — free to type "abc" into a field that will just fail to parse downstream.
    private static TransformConfigFieldSchema Number(
        string key, string label, string? defaultValue = null, string? placeholder = null, bool advanced = false,
        TransformConfigFieldVisibility? visibleWhen = null) =>
        new(key, label, "number", null, defaultValue, placeholder, advanced, visibleWhen);

    private static TransformConfigFieldVisibility OnlyWhen(string key, params string[] values) =>
        new(key, values);

    private static TransformConfigFieldSchema Select(string key, string label, string[] options, string defaultValue, bool advanced = false) =>
        new(key, label, "select", options, defaultValue, IsAdvanced: advanced);

    // A from/to lookup table (add row / key / value) instead of a raw-JSON textarea — the UI builds and
    // parses the same `{"from":"to", ...}` JSON object string this key has always held (ValueCodeMappingNode
    // / StatusEnumCoercionNode both just read it via config.Get(key, "{}") and JSON-deserialize it), so a
    // broken bracket or missing quote — previously a silent typo away — is no longer possible to type at all.
    private static TransformConfigFieldSchema KeyValue(string key, string label) =>
        new(key, label, "keyvalue", null, "{}");

    // A dropdown of common/known values that still allows a genuinely custom one — unlike Select, picking
    // nothing from the list (or the value simply not being one of the presets, e.g. an existing saved rule
    // with a bespoke system URI) reveals a free-text box instead of forcing the value back to a preset.
    // Use this instead of Select whenever "not in the list" is a legitimate answer, not just an oversight —
    // e.g. a rule author's own local/custom code system URI, which a closed dropdown would wrongly block.
    private static TransformConfigFieldSchema Combo(string key, string label, string[] options, string? defaultValue = null, string? placeholder = null, bool advanced = false) =>
        new(key, label, "combo", options, defaultValue, placeholder, advanced);

    private static TransformConfigFieldSchema Checkbox(string key, string label, bool defaultValue, bool advanced = false) =>
        new(key, label, "checkbox", null, defaultValue.ToString(), IsAdvanced: advanced);

    // Same canonical URIs as CodeableConceptBuilderNode.SystemUris (kept in sync by hand — these live in
    // different layers, one a friendly-name lookup table, one a raw dropdown of presets), offered here as
    // presets for a field that otherwise takes any raw URI, so the common cases don't require typing one out.
    private static readonly string[] CommonSystemUris =
    [
        "http://loinc.org", "http://snomed.info/sct", "http://hl7.org/fhir/sid/icd-10-cm",
        "http://www.nlm.nih.gov/research/umls/rxnorm", "http://hl7.org/fhir/sid/us-npi",
        "http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets", "http://www.cms.gov/Medicare/Coding/ICD10",
        "http://hl7.org/fhir/sid/ndc", "http://hl7.org/fhir/sid/cvx",
        "http://unitsofmeasure.org", "http://www.ama-assn.org/go/cpt",
    ];

    // Same UCUM codes as UnitConversionNode.UcumUnitMap (kept in sync by hand, same reasoning as
    // CommonSystemUris above) — offered as presets for sourceUnit/targetUnit, which otherwise take any raw
    // UCUM string. Combo, not Select: the node's `factor` override supports arbitrary analyte-specific
    // conversions (e.g. mg/dL<->mmol/L) this table deliberately doesn't cover.
    private static readonly string[] KnownUcumUnits =
    [
        "kg", "g", "lb_av", "[lb_av]", "[oz_av]", "m", "cm", "mm", "[in_i]", "[ft_i]",
        "Cel", "[degF]", "K", "L", "mL", "dL", "[foz_us]", "mm[Hg]", "kPa",
    ];

    // A representative subset of FHIR R4 resource types, not the full ~150 — Combo, since a Runtime workflow
    // may legitimately target a less common resource type this list hasn't been taught yet.
    private static readonly string[] CommonFhirResourceTypes =
    [
        "Patient", "Practitioner", "PractitionerRole", "RelatedPerson", "Organization", "Location",
        "Encounter", "Observation", "Condition", "Procedure", "MedicationRequest", "MedicationAdministration",
        "MedicationStatement", "AllergyIntolerance", "Immunization", "DiagnosticReport", "DocumentReference",
        "CarePlan", "CareTeam", "Goal", "ServiceRequest", "Coverage", "Claim", "ExplanationOfBenefit",
        "Device", "Specimen", "Group", "Provenance",
    ];

    // Common HL7 v2-0203 identifier type codes — Combo, since the full value set is extensible and a rule
    // author's own local identifier type is a legitimate answer a closed list would wrongly block.
    private static readonly string[] CommonIdentifierTypeCodes = ["MR", "NPI", "SSN", "DL", "PPN", "TAX", "MB", "ACSN"];

    // Full ISO 3166-1 alpha-2 list — closed (Select, not Combo): unlike a code system URI or an identifier
    // type, this is a complete, stable, officially-assigned set with no legitimate "not in the list" case.
    private static readonly string[] Iso3166Alpha2Countries =
    [
        "AD", "AE", "AF", "AG", "AI", "AL", "AM", "AO", "AQ", "AR", "AS", "AT", "AU", "AW", "AX", "AZ",
        "BA", "BB", "BD", "BE", "BF", "BG", "BH", "BI", "BJ", "BL", "BM", "BN", "BO", "BQ", "BR", "BS", "BT", "BV", "BW", "BY", "BZ",
        "CA", "CC", "CD", "CF", "CG", "CH", "CI", "CK", "CL", "CM", "CN", "CO", "CR", "CU", "CV", "CW", "CX", "CY", "CZ",
        "DE", "DJ", "DK", "DM", "DO", "DZ",
        "EC", "EE", "EG", "EH", "ER", "ES", "ET",
        "FI", "FJ", "FK", "FM", "FO", "FR",
        "GA", "GB", "GD", "GE", "GF", "GG", "GH", "GI", "GL", "GM", "GN", "GP", "GQ", "GR", "GS", "GT", "GU", "GW", "GY",
        "HK", "HM", "HN", "HR", "HT", "HU",
        "ID", "IE", "IL", "IM", "IN", "IO", "IQ", "IR", "IS", "IT",
        "JE", "JM", "JO", "JP",
        "KE", "KG", "KH", "KI", "KM", "KN", "KP", "KR", "KW", "KY", "KZ",
        "LA", "LB", "LC", "LI", "LK", "LR", "LS", "LT", "LU", "LV", "LY",
        "MA", "MC", "MD", "ME", "MF", "MG", "MH", "MK", "ML", "MM", "MN", "MO", "MP", "MQ", "MR", "MS", "MT", "MU", "MV", "MW", "MX", "MY", "MZ",
        "NA", "NC", "NE", "NF", "NG", "NI", "NL", "NO", "NP", "NR", "NU", "NZ",
        "OM",
        "PA", "PE", "PF", "PG", "PH", "PK", "PL", "PM", "PN", "PR", "PS", "PT", "PW", "PY",
        "QA",
        "RE", "RO", "RS", "RU", "RW",
        "SA", "SB", "SC", "SD", "SE", "SG", "SH", "SI", "SJ", "SK", "SL", "SM", "SN", "SO", "SR", "SS", "ST", "SV", "SX", "SY", "SZ",
        "TC", "TD", "TF", "TG", "TH", "TJ", "TK", "TL", "TM", "TN", "TO", "TR", "TT", "TV", "TW", "TZ",
        "UA", "UG", "UM", "US", "UY", "UZ",
        "VA", "VC", "VE", "VG", "VI", "VN", "VU",
        "WF", "WS",
        "YE", "YT",
        "ZA", "ZM", "ZW",
    ];

    // The full official FHIR R4 "data-absent-reason" value set — closed (Select): emitting a code outside
    // this set would fail validation on the destination, so a free-text box here could only ever produce
    // either a valid code (typed correctly) or a broken one.
    private static readonly string[] DataAbsentReasonCodes =
    [
        "unknown", "asked-unknown", "temp-unknown", "not-asked", "asked-declined", "masked", "not-applicable",
        "unsupported", "as-text", "error", "not-a-number", "negative-infinity", "positive-infinity",
        "not-performed", "not-permitted",
    ];

    private static readonly IReadOnlyDictionary<TransformNodeType, TransformNodeSchemaDto> Schemas =
        new Dictionary<TransformNodeType, TransformNodeSchemaDto>
        {
            [TransformNodeType.DateTimeFormat] = new(TransformNodeType.DateTimeFormat, "Date/Time Format",
            [
                Select("targetType", "Target type", ["date", "dateTime", "instant"], "dateTime"),
                Checkbox("allowPartialDate", "Allow year/year-month-only precision (non-date target types)", false, advanced: true),
            ]),
            [TransformNodeType.NumberCast] = new(TransformNodeType.NumberCast, "Number Cast",
            [
                Select("targetType", "Target type", ["integer", "decimal"], "decimal"),
                Select("decimalSeparator", "Decimal separator in the source value", ["dot", "comma"], "dot", advanced: true),
            ]),
            [TransformNodeType.BooleanConversion] = new(TransformNodeType.BooleanConversion, "Boolean Conversion",
            [
                Text("trueValues", "True values (comma-separated)", "y,yes,1,t,true,+"),
                Text("falseValues", "False values (comma-separated)", "n,no,0,f,false,-"),
            ]),
            [TransformNodeType.UnitConversion] = new(TransformNodeType.UnitConversion, "Unit Conversion (UCUM)",
            [
                Combo("sourceUnit", "Source unit", KnownUcumUnits, defaultValue: "lb_av"),
                Combo("targetUnit", "Target unit", KnownUcumUnits, defaultValue: "kg"),
                Text("targetCode", "Target UCUM code (optional — defaults to target unit)", placeholder: "e.g. kg", advanced: true),
                Number("factor", "Conversion factor (optional override)", placeholder: "e.g. 18.0182 for mg/dL↔mmol/L glucose", advanced: true),
                Select("direction", "Factor direction", ["multiply", "divide"], "multiply", advanced: true),
                Number("precision", "Decimal places", "1", advanced: true),
            ]),
            [TransformNodeType.QuantityRangeAssembly] = new(TransformNodeType.QuantityRangeAssembly, "Quantity/Range Assembly",
            [
                Text("unit", "Unit", placeholder: "e.g. mg/L"),
            ]),
            [TransformNodeType.RoundingScaling] = new(TransformNodeType.RoundingScaling, "Rounding/Scaling",
            [
                Number("decimalPlaces", "Decimal places", "2"),
                Number("scaleFactor", "Scale factor", "1", advanced: true),
                Number("clampMin", "Clamp minimum (optional)", placeholder: "e.g. 0", advanced: true),
                Number("clampMax", "Clamp maximum (optional)", placeholder: "e.g. 100", advanced: true),
                Select("roundingMode", "Rounding mode", ["halfUp", "halfEven"], "halfUp", advanced: true),
            ]),
            [TransformNodeType.ValueCodeMapping] = new(TransformNodeType.ValueCodeMapping, "Value/Code Mapping",
            [
                KeyValue("map", "Lookup map"),
                Checkbox("caseInsensitive", "Case-insensitive match", true, advanced: true),
                Select("unmatchedPolicy", "When nothing matches", ["null", "passThrough", "error"], "null"),
                Checkbox("emitCoding", "Emit a full Coding (system+code) instead of just the mapped value", false, advanced: true),
                Combo("codingSystem", "Coding system URI (only when emitCoding is on)", CommonSystemUris, placeholder: "e.g. http://hl7.org/fhir/administrative-gender", advanced: true),
            ]),
            [TransformNodeType.CodeableConceptBuilder] = new(TransformNodeType.CodeableConceptBuilder, "CodeableConcept Builder",
            [
                // Closed dropdown, not free text — Nodes/CodesTerminologyNodes.cs's CodeableConceptBuilderNode
                // resolves this key through a fixed SystemUris dictionary and silently falls back to using
                // whatever was typed AS the literal system URI when it isn't a recognized key. A typo (or a
                // correctly-spelled name this dictionary hasn't been taught yet) used to produce a garbage
                // non-URI system value with no error — a closed list makes that specific failure impossible.
                Select("system", "Code system", ["LOINC", "SNOMED", "ICD10", "RXNORM", "NPI", "HCPCS", "ICD10PCS", "NDC", "CVX", "UCUM", "CPT"], "LOINC"),
                Text("display", "Display text (optional — leave blank to resolve from the local terminology DB)", placeholder: "e.g. Glucose", advanced: true),
                Checkbox("resolveDisplayFromTerminology", "Look up real display text from the local terminology DB when Display is blank", true, advanced: true),
                Checkbox("autoDetectSystemOnLocalMiss", "If the code isn't found under Code system above, check other locally-synced systems before giving up (e.g. a wildcard source field mixing SNOMED and ICD-10 codings) — corrects the system to match whichever one actually has the code", false, advanced: true),
                Select("outputShape", "Output shape", ["object", "displayTextOnly"], "object"),
                Checkbox("includeText", "Include CodeableConcept.text (only when Output shape is \"object\")", true, advanced: true),
                Text("additionalCodings", "Additional codings (JSON array of {system,code,display}, optional — only when Output shape is \"object\")", placeholder: "e.g. [{\"system\":\"SNOMED\",\"code\":\"...\"}]", advanced: true),
            ]),
            [TransformNodeType.StatusEnumCoercion] = new(TransformNodeType.StatusEnumCoercion, "Status/Enum Coercion",
            [
                KeyValue("map", "Lookup map"),
                Text("fallback", "Fallback value when nothing matches", "unknown", advanced: true),
            ]),
            [TransformNodeType.ReferenceConstruction] = new(TransformNodeType.ReferenceConstruction, "Reference Construction",
            [
                Combo("resourceType", "Target resource type", CommonFhirResourceTypes, defaultValue: "Patient"),
                Select("style", "Reference style", ["relative", "absolute", "urn", "logical"], "relative"),
                Text("baseUrl", "Base URL (only for absolute style)", placeholder: "e.g. https://fhir.example.com/r4", advanced: true),
                Combo("identifierSystem", "Identifier system URI (only for logical style)", CommonSystemUris, placeholder: "e.g. http://hl7.org/fhir/sid/us-npi", advanced: true),
                Text("display", "Display text (optional)", placeholder: "e.g. Jane Roe", advanced: true),
                Text("allowedTargetTypes", "Allowed target resource types (comma-separated, optional)", placeholder: "e.g. Patient,RelatedPerson", advanced: true),
            ]),
            [TransformNodeType.IdentifierFormatting] = new(TransformNodeType.IdentifierFormatting, "Identifier Formatting",
            [
                Combo("system", "Assigning authority URI", CommonSystemUris, placeholder: "e.g. http://hl7.org/fhir/sid/us-npi"),
                Combo("typeCode", "Identifier type code (e.g. MR, NPI, SSN)", CommonIdentifierTypeCodes, placeholder: "NPI"),
                Number("padLength", "Zero-pad to length (optional)", "0", advanced: true),
            ]),
            [TransformNodeType.HumanNameParsing] = new(TransformNodeType.HumanNameParsing, "HumanName Parsing",
            [
                Select("pattern", "Input pattern", ["FirstLast", "LastFirstMiddle"], "FirstLast"),
                Checkbox("setText", "Set HumanName.text", true, advanced: true),
                Select("use", "Use (optional)", ["", "official", "usual", "nickname", "maiden"], "", advanced: true),
                Text("prefixTokens", "Recognized prefixes (comma-separated)", "Dr,Mr,Mrs,Ms,Miss", advanced: true),
                Text("suffixTokens", "Recognized suffixes (comma-separated)", "Jr,Sr,II,III,IV,MD,PhD", advanced: true),
            ]),
            [TransformNodeType.AddressParsing] = new(TransformNodeType.AddressParsing, "Address Parsing",
            [
                Select("use", "Use", ["home", "work", "temp", "old"], "home"),
                Select("type", "Type (optional)", ["", "postal", "physical", "both"], "", advanced: true),
                Select("country", "Country (ISO-3166 alpha-2)", Iso3166Alpha2Countries, "US"),
            ]),
            [TransformNodeType.TelecomNormalization] = new(TransformNodeType.TelecomNormalization, "Telecom Normalization",
            [
                Select("use", "Use", ["home", "work", "mobile", "temp", "old"], "mobile"),
                Select("system", "Force system (optional — overrides phone/email auto-detect)", ["", "fax", "url"], "", advanced: true),
                Select("region", "Region for parsing phone numbers (ISO-3166 alpha-2)", Iso3166Alpha2Countries, "US"),
                Number("rank", "Rank (optional — order when this is one of several)", placeholder: "e.g. 1", advanced: true),
            ]),
            [TransformNodeType.StringNormalization] = new(TransformNodeType.StringNormalization, "String Normalization",
            [
                Select("case", "Case", ["none", "upper", "lower", "title"], "none"),
                Text("regexPattern", "Regex replace — pattern (optional)", placeholder: "e.g. \\s+ — leave blank to skip", advanced: true),
                Text("regexReplacement", "Regex replace — replacement (optional)", placeholder: "e.g. \" \" (single space)", advanced: true),
                Number("maxLength", "Max length (optional, 0 = no limit)", "0", advanced: true),
                Checkbox("stripDiacritics", "Strip diacritics (é→e) and control characters", true, advanced: true),
            ]),
            [TransformNodeType.ConcatenationTemplating] = new(TransformNodeType.ConcatenationTemplating, "Concatenation/Templating",
            [
                Select("mode", "Mode", ["concat", "split"], "concat"),
                Text("separator", "Join separator (concat mode, ignored when a template is set)", " "),
                Text("template", "Template with {0} {1}... placeholders (concat mode, optional)", placeholder: "e.g. {0} {1}, MD", advanced: true),
                Text("splitDelimiter", "Split delimiter or regex (split mode)", ","),
                Checkbox("splitIsRegex", "Treat split delimiter as a regex", false, advanced: true),
            ]),
            [TransformNodeType.ArrayListOperations] = new(TransformNodeType.ArrayListOperations, "Array/List Operations",
            [
                Select("operation", "Operation", ["first", "last", "nth", "dedupe", "join", "count", "filter", "flatten"], "first"),
                Number("index", "Index (nth operation)", "0"),
                Text("separator", "Join separator (join operation)", ","),
                Text("predicateField", "Predicate field (filter operation, optional — omit to compare items directly)", placeholder: "e.g. system", advanced: true),
                Text("predicateValue", "Predicate value to match (filter operation)", placeholder: "e.g. http://hl7.org/fhir/sid/us-npi", advanced: true),
            ]),
            [TransformNodeType.DefaultNullHandling] = new(TransformNodeType.DefaultNullHandling, "Default/Null Handling",
            [
                Text("default", "Default value when nothing present (optional)", placeholder: "e.g. unknown", advanced: true),
                Text("sentinels", "Sentinel values treated as empty (comma-separated)", "N/A,UNKNOWN,9999", advanced: true),
                Checkbox("addDataAbsentReason", "Add a data-absent-reason marker when nothing present and no default", false, advanced: true),
                Select("dataAbsentReasonCode", "Data-absent-reason code", DataAbsentReasonCodes, "unknown", advanced: true),
            ]),
            [TransformNodeType.DateMathAge] = new(TransformNodeType.DateMathAge, "Date Math/Age",
            [
                Select("operation", "Operation", ["age", "add", "shift"], "age"),
                Text("referenceDate", "Reference date (age operation, optional — defaults to today)", placeholder: "e.g. 2026-01-01", advanced: true),
                // Kept out of Advanced Options despite having a default — this is a HIPAA Safe Harbor
                // compliance control, not a fine-tuning knob; burying it would make it too easy to miss that
                // it's on (or to leave it off) when configuring a de-identification rule.
                Checkbox("redactOver89", "Redact ages over 89 (Safe Harbor)", true),
                Text("duration", "ISO-8601 duration, e.g. P1Y6M or P30D (add operation)", "P0D", advanced: true),
                Number("days", "Fixed day offset — used when no vault secret/patient id is available (shift operation)", "0", advanced: true),
                Number("maxShiftDays", "Max seeded shift magnitude in days (shift operation)", "60", advanced: true),
            ]),
            [TransformNodeType.HashingMasking] = new(TransformNodeType.HashingMasking, "Hashing/Masking",
            [
                Select("mode", "Mode", ["hash", "mask", "redact"], "mask"),
                // Each applies to exactly one mode — hashing has nothing to keep visible, and only redact
                // substitutes a token. Scoped rather than merely marked advanced so a hash rule's stored
                // config says "mode = hash" and nothing else.
                Number("keepLength", "End characters to keep visible", "4", advanced: true,
                    visibleWhen: OnlyWhen("mode", "mask")),
                Text("token", "Replacement token", placeholder: "e.g. [REDACTED]", advanced: true,
                    visibleWhen: OnlyWhen("mode", "redact")),
            ]),
        };

    public static IReadOnlyList<TransformNodeSchemaDto> All => Schemas.Values.ToList();

    public static TransformNodeSchemaDto? Get(TransformNodeType nodeType) =>
        Schemas.GetValueOrDefault(nodeType);

    /// <summary>The default config a newly-added step of this node type should start with, ready to use
    /// as-is or tweak — never an empty <c>{}</c>.</summary>
    public static Dictionary<string, string> DefaultConfig(TransformNodeType nodeType)
    {
        var schema = Get(nodeType);
        if (schema is null)
        {
            return [];
        }

        return schema.Fields
            .Where(f => f.DefaultValue is not null)
            .ToDictionary(f => f.Key, f => f.DefaultValue!);
    }
}
