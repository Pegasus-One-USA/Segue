using System.Diagnostics;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Transforms;

/// <summary>One rule application, captured for lineage and for surfacing a failed rule without failing the
/// record. <paramref name="Before"/>/<paramref name="After"/> are JSON-serialized so a value of any shape
/// (scalar, CodeableConcept object, array) records identically.</summary>
public sealed record FhirResourceTransformHop(
    string SourceField,
    string WriteBackPath,
    int NodeOrder,
    TransformNodeType NodeType,
    string ConfigJson,
    string? Before,
    string? After,
    bool Success,
    string? Error,
    double DurationMs,
    DateTimeOffset ExecutedAtUtc);

/// <summary>Result of transforming one resource. <paramref name="Json"/> is the resource to carry forward —
/// the original, unchanged, when no rule produced a value.</summary>
public sealed record FhirResourceTransformResult(
    string Json,
    IReadOnlyList<FhirResourceTransformHop> Hops);

/// <summary>
/// Runs <see cref="TransformExecutionPhase.FhirResource"/> rules against a whole FHIR resource: read a value at
/// the rule's FHIR path, run it through the same 20-node registry the mapped-column path uses, and write the
/// result back into the resource's own JSON.
///
/// This exists because a FHIR-native destination (Aidbox/Medplum/Azure FHIR) persists the resource itself, so
/// there is no mapped column for a PostMapping rule to attach to — without it those pipelines can only ship
/// Epic's output verbatim. Deliberately reuses <see cref="ITransformNodeRegistry"/>,
/// <see cref="TransformNodeApplier"/> and <see cref="TransformNullPolicy"/> rather than reimplementing them, so
/// a rule behaves identically whether it lands in a SQL column or back into the resource.
/// </summary>
public interface IFhirResourceTransformService
{
    Task<FhirResourceTransformResult> TransformAsync(
        string resourceJson,
        string resourceType,
        string resourceId,
        DestinationType destinationType,
        Guid? resourcePipelineRouteId,
        string? sourceSystem,
        CancellationToken cancellationToken);
}

public sealed class FhirResourceTransformService : IFhirResourceTransformService
{
    private readonly IFhirResourceRuleResolver _resolver;
    private readonly ITransformNodeRegistry _nodeRegistry;
    private readonly IAppSecretAccessor? _secretAccessor;

    public FhirResourceTransformService(
        IFhirResourceRuleResolver resolver,
        ITransformNodeRegistry nodeRegistry,
        IAppSecretAccessor? secretAccessor = null)
    {
        _resolver = resolver;
        _nodeRegistry = nodeRegistry;
        _secretAccessor = secretAccessor;
    }

    public async Task<FhirResourceTransformResult> TransformAsync(
        string resourceJson,
        string resourceType,
        string resourceId,
        DestinationType destinationType,
        Guid? resourcePipelineRouteId,
        string? sourceSystem,
        CancellationToken cancellationToken)
    {
        var sourceFields = await _resolver.ResolveSourceFieldsAsync(
            resourceType, destinationType, resourcePipelineRouteId, sourceSystem, cancellationToken);

        if (sourceFields.Count == 0)
        {
            return new FhirResourceTransformResult(resourceJson, []);
        }

        var hops = new List<FhirResourceTransformHop>();
        // Collected across every path, then applied in one pass at the end — patching as we went would let one
        // rule's output become the next rule's input by accident, which the per-path chains above already
        // express explicitly when that's what the author wanted.
        var patches = new List<(string Path, object? Value)>();

        foreach (var sourceField in sourceFields)
        {
            var rules = await _resolver.ResolveAsync(
                resourceType, sourceField, destinationType, resourcePipelineRouteId, sourceSystem, cancellationToken);

            if (rules.Count == 0)
            {
                continue;
            }

            // SourceField is persisted in the rule-authoring UI's "ResourceType.field" convention (e.g.
            // "Observation.valueQuantity.value" — see ToRuleAuthoringSourceFieldFormat in the mapping executor),
            // while the resource's own JSON has no such prefix. Strip it before reading, or every rule silently
            // resolves to null and no transformation ever fires.
            var readPath = StripResourceTypePrefix(sourceField, resourceType);
            var currentValue = FhirJsonPathReader.Read(resourceJson, readPath);
            // Defaults to the path the rule read. Correct for every scalar-in/scalar-out node; a
            // structure-building node (CodeableConceptBuilder, ReferenceConstruction, UnitConversion in Quantity
            // mode) sets FhirWriteBackJsonPath to the PARENT instead — a rule reading "code.coding.code" but
            // emitting a whole CodeableConcept belongs at "code", not back into the bare code leaf.
            var writeBackPath = readPath;
            var nodeOrder = 0;
            var produced = false;

            foreach (var rule in rules)
            {
                if (!string.IsNullOrWhiteSpace(rule.FhirWriteBackJsonPath))
                {
                    writeBackPath = rule.FhirWriteBackJsonPath;
                }

                if (TransformNullPolicy.ShouldShortCircuit(rule, currentValue))
                {
                    currentValue = TransformNullPolicy.Apply(rule, currentValue, out var stopChain);
                    // A Default policy substitutes a real value, so the chain has something to write back even
                    // though the node never ran; Skip/Error leave it missing and there is nothing to patch.
                    produced |= rule.OnNull == NullPolicy.Default;
                    if (stopChain)
                    {
                        break;
                    }

                    continue;
                }

                Dictionary<string, string> config;
                try
                {
                    config = JsonSerializer.Deserialize<Dictionary<string, string>>(rule.ConfigJson) ?? [];
                }
                catch (JsonException)
                {
                    // A corrupt ConfigJson is a problem with the rule row, not this resource — skip the one
                    // rule rather than let a parse error take down the whole batch.
                    hops.Add(new FhirResourceTransformHop(
                        sourceField, writeBackPath, nodeOrder++, rule.NodeType, rule.ConfigJson,
                        Serialize(currentValue), null, false, "rule configuration is not valid JSON",
                        0, DateTimeOffset.UtcNow));
                    continue;
                }

                config[ReservedTransformConfigKeys.DestinationType] = destinationType.ToString();
                if (rule.NodeType == TransformNodeType.DateMathAge
                    && string.Equals(resourceType, "Patient", StringComparison.OrdinalIgnoreCase))
                {
                    // The per-patient seeded shift needs "which patient" — for a Patient resource that's its own
                    // id. Other resource types would need reference resolution this loop doesn't do, and fall
                    // back to the rule's fixed `days` config, exactly as the mapped-column path does.
                    config[ReservedTransformConfigKeys.PatientId] = resourceId;
                }

                if (rule.NodeType == TransformNodeType.CodeableConceptBuilder)
                {
                    var siblingDisplay = FhirSourceJsonPatcher.TryReadSiblingDisplay(resourceJson, readPath);
                    if (siblingDisplay is not null)
                    {
                        config[ReservedTransformConfigKeys.SourceDisplayHint] = siblingDisplay;
                    }
                }

                // The unit hint has to be fed on THIS path too, not only the Runtime pipeline's mapped-column
                // loop. A rule reads "$.valueQuantity.value", so the node receives the scalar 187 and its
                // whole-Quantity branch never fires — leaving the rule's own (blank) unit as the only source
                // of one, which is exactly the "unit": "" this branch set out to fix. It matters more here
                // than on the mapped-column path: FhirWriteBackJsonPath replaces the whole element, so a
                // FHIR-native destination does not merely miss the unit, it has the source's own unit erased.
                if (rule.NodeType == TransformNodeType.QuantityRangeAssembly)
                {
                    var siblingUnit = FhirSourceJsonPatcher.TryReadSiblingQuantityUnit(resourceJson, readPath);
                    if (siblingUnit is not null)
                    {
                        config[ReservedTransformConfigKeys.SourceUnitHint] = siblingUnit;
                    }
                }

                var secret = rule.NodeType is TransformNodeType.HashingMasking or TransformNodeType.DateMathAge
                    ? _secretAccessor?.TransformHashingKey
                    : null;

                var before = currentValue;
                var executedAtUtc = DateTimeOffset.UtcNow;
                var stopwatch = Stopwatch.StartNew();
                var result = await TransformNodeApplier.ExecuteWithArrayModeAsync(
                    _nodeRegistry.Get(rule.NodeType), currentValue, config, secret, rule.ArrayMode, cancellationToken);
                stopwatch.Stop();

                hops.Add(new FhirResourceTransformHop(
                    sourceField, writeBackPath, nodeOrder++, rule.NodeType, rule.ConfigJson,
                    Serialize(before), result.Success ? Serialize(result.Value) : null,
                    result.Success, result.Error, stopwatch.Elapsed.TotalMilliseconds, executedAtUtc));

                if (result.Success)
                {
                    currentValue = result.Value;
                    produced = true;
                    continue;
                }

                // Error policy mirrors the mapped-column path: PassThrough keeps the value the chain had and
                // carries on, everything else abandons this path and leaves the resource's own value in place.
                if (rule.ErrorPolicy != TransformErrorPolicy.PassThrough)
                {
                    produced = false;
                    break;
                }
            }

            // Never patch a null back into the resource. Several nodes legitimately return Ok(null) when the
            // input isn't something they can convert (RoundingScaling on a non-numeric value, for instance) —
            // writing that through would put an explicit JSON null into a FHIR element, which a strict server
            // rejects outright. Leaving the original value in place is both safer and closer to intent: a rule
            // that produced nothing has nothing to say about this element.
            if (produced && currentValue is not null)
            {
                patches.Add((writeBackPath, currentValue));
            }
        }

        var json = patches.Count > 0
            ? FhirSourceJsonPatcher.ApplyPatches(resourceJson, patches) ?? resourceJson
            : resourceJson;

        return new FhirResourceTransformResult(json, hops);
    }

    /// <summary>Removes a leading "<c>{resourceType}.</c>" from a rule's SourceField. Only the exact resource
    /// type is stripped: a path that genuinely starts with a different word (or with the resource type as part
    /// of a longer segment) is left alone.</summary>
    public static string StripResourceTypePrefix(string sourceField, string resourceType)
    {
        var prefix = resourceType + ".";
        return sourceField.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? sourceField[prefix.Length..]
            : sourceField;
    }

    /// <summary>Serializes a hop's before/after value for display. Uses the relaxed encoder so a value
    /// containing &lt;, &gt; or &amp; — a reference range like "&lt;=200", say — reads as itself in the
    /// preview rather than as an escaped <=200; the default HTML-escaping encoder exists to protect JSON
    /// embedded in a page, which this never is.</summary>
    private static string? Serialize(object? value) => value switch
    {
        null => null,
        string s => s,
        _ => JsonSerializer.Serialize(value, RelaxedJsonOptions),
    };

    private static readonly JsonSerializerOptions RelaxedJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
