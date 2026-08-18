using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Terminology;
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
        // Added alongside the terminology auto-poll work — HCPCS/ICD10PCS use the FHIR-registered "sid" URN
        // pattern by convention; verify against https://www.hl7.org/fhir/terminologies-systems.html before
        // treating these as gospel if a downstream consumer ever flags an unrecognized system.
        ["HCPCS"] = "urn:oid:2.16.840.1.113883.6.285",
        ["ICD10PCS"] = "http://hl7.org/fhir/sid/icd-10-pcs",
        ["NDC"] = "http://hl7.org/fhir/sid/ndc",
        ["CVX"] = "http://hl7.org/fhir/sid/cvx",
        ["UCUM"] = "http://unitsofmeasure.org",
        ["CPT"] = "http://www.ama-assn.org/go/cpt",
    };

    private readonly ITerminologyLookupService? _terminologyLookupService;

    public CodeableConceptBuilderNode(ITerminologyLookupService? terminologyLookupService = null)
    {
        _terminologyLookupService = terminologyLookupService;
    }

    public TransformNodeType NodeType => TransformNodeType.CodeableConceptBuilder;

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

        // Display resolution order: (1) an author-hand-typed value always wins outright; (2) the local
        // terminology DB, when lookup isn't explicitly disabled; (3) whatever display the SOURCE resource's own
        // JSON already carried alongside this code (e.g. Epic's own Coding.display) — only available in a real
        // pipeline run, never in TransformationRuleService's preview, since that needs the whole resource JSON;
        // (4) the bare code itself, same as this node's original, still-documented fallback. Each tier only
        // fires if every earlier one had nothing — a DB/JSON miss never fails the transform, it just falls
        // through.
        if (display is null && _terminologyLookupService is not null && config.GetBool("resolveDisplayFromTerminology", true))
        {
            var lookup = await _terminologyLookupService.LookupAsync(systemUri, code, cancellationToken);
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
        if (string.Equals(config.Get("outputShape", "object"), "displayTextOnly", StringComparison.OrdinalIgnoreCase))
        {
            return TransformResult.Ok(display ?? code);
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

        return TransformResult.Ok(concept);
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
