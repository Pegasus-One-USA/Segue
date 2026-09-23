using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Transforms.Nodes;

/// <summary>7. Value / Code Mapping (Lookup Translation) — a static field-grain ConceptMap.
/// <c>config["map"]</c> is a JSON object string, e.g. <c>{"M":"male","F":"female"}</c>.</summary>
public sealed class ValueCodeMappingNode : ITransformNode
{
    public TransformNodeType NodeType => TransformNodeType.ValueCodeMapping;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var raw = value?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TransformResult.Ok(null);
        }

        var map = ParseMap(config.Get("map", "{}"));
        var caseInsensitive = config.GetBool("caseInsensitive", true);
        var comparer = caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        var match = map.FirstOrDefault(kv => comparer.Equals(kv.Key, raw));
        if (match.Key is not null)
        {
            if (!config.GetBool("emitCoding", false))
            {
                return TransformResult.Ok(match.Value);
            }

            var coding = new JsonObject { ["code"] = match.Value };
            var system = config.GetOrNull("codingSystem");
            if (system is not null)
            {
                coding["system"] = system;
            }

            return TransformResult.Ok(coding);
        }

        return config.Get("unmatchedPolicy", "null") switch
        {
            "passThrough" => TransformResult.Ok(raw),
            "error" => TransformResult.Fail($"No mapping configured for '{raw}'."),
            _ => TransformResult.Ok(null)
        };
    }

    internal static Dictionary<string, string> ParseMap(string mapJson)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(mapJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>8. CodeableConcept / Coding Builder — assembles a coded element from a bare code + config.</summary>
public sealed class CodeableConceptBuilderNode : ITransformNode
{
    // Friendly name -> canonical system URI. If a typed/selected key isn't found here, Execute() below falls
    // back to using it as the literal system URI verbatim — which silently produces a non-URI garbage value
    // for anything not in this list (confirmed: this list predates the HCPCS/ICD10PCS/NDC/CVX/UCUM/CPT
    // terminology work, so those previously fell into exactly that trap even when spelled correctly). The
    // portal's CodeableConcept Builder field now renders this as a closed dropdown (see
    // TransformNodeConfigSchemas.CodeableConceptBuilder's "system" field) rather than free text, so a typo
    // can no longer select a key that isn't here.
    private static readonly IReadOnlyDictionary<string, string> SystemUris = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["LOINC"] = "http://loinc.org",
        ["SNOMED"] = "http://snomed.info/sct",
        ["ICD10"] = "http://hl7.org/fhir/sid/icd-10-cm",
        ["RXNORM"] = "http://www.nlm.nih.gov/research/umls/rxnorm",
        ["NPI"] = "http://hl7.org/fhir/sid/us-npi",
        // Confirmed against HL7's official Terminology registry (terminology.hl7.org) — these are the
        // canonical system URIs, and must match the url used when loading each CodeSystem into the
        // terminology server (see HapiHcpcsTerminologySyncService/HapiIcd10PcsTerminologySyncService),
        // or a lookup here silently misses even though the code is genuinely loaded.
        ["HCPCS"] = "http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets",
        ["ICD10PCS"] = "http://www.cms.gov/Medicare/Coding/ICD10",
        ["NDC"] = "http://hl7.org/fhir/sid/ndc",
        ["CVX"] = "http://hl7.org/fhir/sid/cvx",
        ["UCUM"] = "http://unitsofmeasure.org",
        ["CPT"] = "http://www.ama-assn.org/go/cpt",
        // The four systems that are synced by the terminology server but were missing here, so a rule
        // authored against them fell through to using the literal dropdown key as the system URI.
        ["ICD11MMS"] = "http://id.who.int/icd/release/11/mms",
        ["ICPC3"] = "http://terminology.hl7.org/CodeSystem/ICPC-3",
        ["DCM"] = "http://dicom.nema.org/resources/ontology/DCM",
        ["MESH"] = "https://www.nlm.nih.gov/mesh",
    };

    private readonly ITerminologyLookupService? _terminologyLookupService;

    public CodeableConceptBuilderNode(ITerminologyLookupService? terminologyLookupService = null)
    {
        _terminologyLookupService = terminologyLookupService;
    }

    public TransformNodeType NodeType => TransformNodeType.CodeableConceptBuilder;

    /// <summary>Which of the three description fields an output shape asks for, or null when the shape is
    /// not a description shape at all ("object"/"displayTextOnly").</summary>
    private static DescriptionField? TryGetDescriptionShape(string outputShape) => outputShape.ToLowerInvariant() switch
    {
        "shortdescription" => DescriptionField.Short,
        "longdescription" => DescriptionField.Long,
        "longcommonname" => DescriptionField.LongCommonName,
        _ => null,
    };

    private enum DescriptionField
    {
        Short,
        Long,
        LongCommonName,
    }

    /// <summary>
    /// Picks the requested description field, falling back across the other two when it is null for this
    /// concept, then to the resolved display, then to the bare code.
    ///
    /// The fallback ORDER is deliberate and differs per requested field: it runs from the most similar
    /// alternative to the least. A request for the short description falls back to the long common name
    /// before the long description (a common name is closer to a label than a full definition is), while a
    /// request for a long form prefers the other long form before dropping to the short one. Without this,
    /// asking for a short description on a source that has none would return a multi-sentence definition
    /// into a column sized for a label.
    /// </summary>
    private static string ResolveDescription(
        DescriptionField requested, TerminologyLookupResult? lookup, string? display, string code)
    {
        var shortDescription = NullIfBlank(lookup?.ShortDescription);
        var longDescription = NullIfBlank(lookup?.LongDescription);
        var longCommonName = NullIfBlank(lookup?.LongCommonName);

        var candidate = requested switch
        {
            DescriptionField.Short => shortDescription ?? longCommonName ?? longDescription,
            DescriptionField.Long => longDescription ?? longCommonName ?? shortDescription,
            DescriptionField.LongCommonName => longCommonName ?? longDescription ?? shortDescription,
            _ => null,
        };

        return candidate ?? NullIfBlank(display) ?? code;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret) =>
        ExecuteAsync(value, config, secret).GetAwaiter().GetResult();

    public async Task<TransformResult> ExecuteAsync(
        object? value, IReadOnlyDictionary<string, string> config, string? secret, CancellationToken cancellationToken = default)
    {
        var code = value?.ToString();
        if (string.IsNullOrWhiteSpace(code))
        {
            return TransformResult.Ok(null);
        }

        var systemKey = config.Get("system");
        var systemUri = SystemUris.GetValueOrDefault(systemKey, systemKey);
        var display = config.GetOrNull("display");
        string? resolvedSystemOverride = null;
        // Held beyond the display-resolution block below so the description output shapes can read the
        // extra fields off the same lookup instead of issuing a second identical query.
        TerminologyLookupResult? lookupForDescriptions = null;

        // Display resolution order: (1) an author-hand-typed value always wins outright; (2) the local
        // terminology DB, when lookup isn't explicitly disabled; (3) whatever display the SOURCE resource's own
        // JSON already carried alongside this code (e.g. Epic's own Coding.display) — only available in a real
        // pipeline run, never in TransformationRuleService's preview, since that needs the whole resource JSON;
        // (4) the bare code itself, same as this node's original, still-documented fallback. Each tier only
        // fires if every earlier one had nothing — a DB/JSON miss never fails the transform, it just falls
        // through.
        if (display is null && _terminologyLookupService is not null && config.GetBool("resolveDisplayFromTerminology", true))
        {
            TerminologyLookupResult? lookup = null;

            // Opt-in: a wildcard source field (e.g. Condition.code.coding[*].code) can extract codes from
            // MULTIPLE codings on the same resource that use DIFFERENT systems (a SNOMED coding alongside an
            // ICD-10-CM one is common from Epic) — one fixed "system" config can't be right for all of them.
            // Rather than let a genuine mismatch fall through to a 120s network timeout, check every
            // locally-synced system first; if the code turns up under a different one, use THAT system+display
            // (never the configured-but-wrong one paired with a borrowed display — that would be self-
            // contradictory FHIR) and surface the substitution via ResolvedSystemOverride for lineage.
            if (config.GetBool("autoDetectSystemOnLocalMiss", false))
            {
                lookup = await _terminologyLookupService.LookupAnyLocalSystemAsync(code, cancellationToken);
                if (lookup is not null && !string.Equals(lookup.System, systemUri, StringComparison.OrdinalIgnoreCase))
                {
                    resolvedSystemOverride = lookup.System;
                    systemUri = lookup.System;
                }
            }

            lookup ??= await _terminologyLookupService.LookupAsync(systemUri, code, cancellationToken);
            lookupForDescriptions = lookup;
            if (!string.IsNullOrWhiteSpace(lookup?.Display))
            {
                display = lookup.Display;
            }
        }

        if (display is null)
        {
            display = config.GetOrNull(ReservedTransformConfigKeys.SourceDisplayHint);
        }

        // A flat/tabular destination column (a SQL varchar report column, a CSV field) often wants just the
        // human-readable text, not the machine-readable system+code structure — same reasoning as
        // ValueCodeMappingNode's emitCoding checkbox, just defaulting the opposite way since this node's whole
        // purpose is normally to BUILD the structured concept. Skips building the coding array/additionalCodings
        // entirely rather than building it and discarding it.
        var outputShape = config.Get("outputShape", "object");

        if (string.Equals(outputShape, "displayTextOnly", StringComparison.OrdinalIgnoreCase))
        {
            return TransformResult.Ok(display ?? code, resolvedSystemOverride);
        }

        // The three description shapes emit one plain string drawn from the local terminology store,
        // rather than the resolved display. Field availability genuinely varies by source (LOINC
        // publishes a short name, a long common name and a definition; UCUM publishes only a unit name),
        // so the requested field is often null for a perfectly valid code. Rather than emit a blank
        // column, fall back across the other two description fields, then the display, then the code —
        // so the output is always the most specific text actually available for that concept.
        if (TryGetDescriptionShape(outputShape) is { } requestedField)
        {
            var descriptions = lookupForDescriptions;
            var resolved = ResolveDescription(requestedField, descriptions, display, code);
            return TransformResult.Ok(resolved, resolvedSystemOverride);
        }

        var primaryCoding = new JsonObject { ["system"] = systemUri, ["code"] = code };
        if (display is not null)
        {
            primaryCoding["display"] = display;
        }

        var codings = new JsonArray(primaryCoding);

        // Optional second/third coding for the same concept (e.g. a local code alongside its standard
        // translation) — a JSON array of {system,code,display} objects, kept separate from the primary
        // system/code/display fields above so the common single-coding case stays a flat config, not JSON.
        var additional = config.GetOrNull("additionalCodings");
        if (additional is not null)
        {
            try
            {
                var extra = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string>>>(additional) ?? [];
                foreach (var entry in extra)
                {
                    var extraSystemKey = entry.GetValueOrDefault("system");
                    var extraCoding = new JsonObject
                    {
                        ["system"] = extraSystemKey is null ? null : SystemUris.GetValueOrDefault(extraSystemKey, extraSystemKey),
                        ["code"] = entry.GetValueOrDefault("code")
                    };
                    if (entry.TryGetValue("display", out var extraDisplay))
                    {
                        extraCoding["display"] = extraDisplay;
                    }

                    codings.Add(extraCoding);
                }
            }
            catch (System.Text.Json.JsonException)
            {
                return TransformResult.Fail($"'additionalCodings' is not valid JSON: '{additional}'.");
            }
        }

        var concept = new JsonObject { ["coding"] = codings };
        if (config.GetBool("includeText", true))
        {
            concept["text"] = display ?? code;
        }

        return TransformResult.Ok(concept, resolvedSystemOverride);
    }
}

/// <summary>9. Status / Enum Coercion (Required Binding) — like <see cref="ValueCodeMappingNode"/> but never
/// emits an out-of-value-set code: unmatched input always falls back to <c>config["fallback"]</c> (default
/// "unknown") rather than passing through or erroring.</summary>
public sealed class StatusEnumCoercionNode : ITransformNode
{
    public TransformNodeType NodeType => TransformNodeType.StatusEnumCoercion;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var raw = value?.ToString()?.Trim();
        var fallback = config.Get("fallback", "unknown");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TransformResult.Ok(fallback);
        }

        var map = ValueCodeMappingNode.ParseMap(config.Get("map", "{}"));
        var match = map.FirstOrDefault(kv => string.Equals(kv.Key, raw, StringComparison.OrdinalIgnoreCase));
        return TransformResult.Ok(match.Key is not null ? match.Value : fallback);
    }
}
