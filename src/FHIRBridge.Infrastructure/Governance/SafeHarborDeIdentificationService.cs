using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// HIPAA Safe Harbor-style de-identification. Applies the pre-mapping <c>TransformationRule</c> rows belonging
/// to <see cref="DeIdentificationRequest.ProfileId"/> as a set of FHIR-path redaction rules over the raw resource
/// JSON: direct identifiers are removed, hashed, or masked, and dates/ZIPs are generalized. A null
/// <see cref="DeIdentificationRequest.ProfileId"/> means "no profile assigned" — the resource passes through
/// unchanged, since profiles (not one tenant-wide default) are the unit of "what redaction applies here."
/// </summary>
public sealed class SafeHarborDeIdentificationService : IDeIdentificationService
{
    private readonly ITransformationRuleRepository _ruleRepository;
    private readonly ILogger<SafeHarborDeIdentificationService> _logger;

    /// <summary>Per-resource-type "does this profile redact the id?" lookups, memoized for this scoped
    /// instance — see <see cref="GetIdRedactionAsync"/>.</summary>
    private readonly Dictionary<(Guid ProfileId, string ResourceType), IdRedaction?> _idRedactionCache = new();

    public SafeHarborDeIdentificationService(
        ITransformationRuleRepository ruleRepository,
        ILogger<SafeHarborDeIdentificationService>? logger = null)
    {
        _ruleRepository = ruleRepository;
        _logger = logger ?? NullLogger<SafeHarborDeIdentificationService>.Instance;
    }

    public async Task<DeIdentificationResult> DeIdentifyAsync(DeIdentificationRequest request, CancellationToken cancellationToken)
    {
        if (request.ProfileId is not { } profileId)
        {
            return new DeIdentificationResult(request.RawJson, []);
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(request.RawJson);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "De-identification skipped: {ResourceType} is not valid JSON.", request.ResourceType);
            return new DeIdentificationResult(request.RawJson, []);
        }

        if (root is not JsonObject resource)
        {
            return new DeIdentificationResult(request.RawJson, []);
        }

        var hops = new List<DeIdentificationFieldHop>();
        var rules = await _ruleRepository.GetPreMappingRulesAsync(profileId, request.ResourceType, cancellationToken);
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.SourceField) || !TryReadStrategy(rule.ConfigJson, out var strategy))
            {
                continue;
            }

            // Captured before mutating so the hop's "before" value reflects what this field actually held prior
            // to this rule — TryReadValueAt returns null both for "path doesn't exist" and "value was itself
            // null," which is fine here: either way there is nothing meaningful to redact or report as changed.
            var pathSegments = ParseSourceFieldPath(rule.SourceField, request.ResourceType);
            var beforeValue = TryReadValueAt(resource, pathSegments, 0);
            if (beforeValue is null)
            {
                continue;
            }

            ApplyPath(resource, pathSegments, 0, strategy, rule.ConfigJson);
            var afterValue = TryReadValueAt(resource, pathSegments, 0);

            hops.Add(new DeIdentificationFieldHop(
                rule.SourceField,
                strategy.ToString(),
                rule.ConfigJson,
                beforeValue,
                afterValue,
                true,
                null));
        }

        // Keep every reference pointing at a redacted resource in step with that resource's new id (e.g. an
        // Observation's subject.reference following Patient.id). Runs after this resource's own rules so a
        // self-reference sees the final value.
        hops.AddRange(await RewriteReferencesAsync(resource, profileId, string.Empty, cancellationToken));

        return new DeIdentificationResult(resource.ToJsonString(), hops);
    }

    /// <summary>
    /// Rewrites every FHIR reference in the resource that points at a resource type whose <c>id</c> this profile
    /// redacts, applying the same rule to the id inside the reference.
    ///
    /// Without this, redacting an id silently breaks the graph: <c>Patient.id</c> becomes <c>****eiw3</c> while
    /// every child still carries <c>subject.reference = "Patient/&lt;original&gt;"</c>. On SQL that is a join that
    /// no longer matches — no error, just orphaned rows; on a FHIR server it is a dangling reference the write
    /// either rejects or stores broken.
    ///
    /// It works without any batch state or original→redacted crosswalk because the strategies are pure
    /// functions of the input: hashing or masking "abc" yields the same result wherever it is encountered. That
    /// matters beyond convenience — a reference to a resource that is NOT in the current batch (or was written
    /// by an earlier run) still lands on exactly the same value, so links stay intact across runs rather than
    /// only within one.
    ///
    /// <see cref="DeIdentificationStrategy.Remove"/> is deliberately skipped: it produces no replacement value,
    /// so there is nothing to point a reference at. Note also that
    /// <see cref="DeIdentificationStrategy.Redact"/> maps every id to one constant token, which keeps references
    /// resolvable but collapses all resources of that type onto a single identity — correct per the configured
    /// rule, and a reason to prefer Hash when linkage is meant to survive.
    /// </summary>
    private async Task<List<DeIdentificationFieldHop>> RewriteReferencesAsync(
        JsonNode? node,
        Guid profileId,
        string path,
        CancellationToken cancellationToken)
    {
        var hops = new List<DeIdentificationFieldHop>();

        switch (node)
        {
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    hops.AddRange(await RewriteReferencesAsync(array[i], profileId, path, cancellationToken));
                }

                break;

            case JsonObject obj:
                foreach (var property in obj.ToList())
                {
                    var childPath = path.Length == 0 ? property.Key : $"{path}.{property.Key}";

                    // Reference.reference is the only place a "Type/id" pointer lives in FHIR R4, so keying off
                    // the property name avoids needing a resource schema here.
                    if (property.Key == "reference"
                        && property.Value is JsonValue value
                        && value.TryGetValue<string>(out var reference))
                    {
                        var hop = await TryRewriteReferenceAsync(obj, childPath, reference, profileId, cancellationToken);
                        if (hop is not null)
                        {
                            hops.Add(hop);
                        }

                        continue;
                    }

                    hops.AddRange(await RewriteReferencesAsync(property.Value, profileId, childPath, cancellationToken));
                }

                break;
        }

        return hops;
    }

    private async Task<DeIdentificationFieldHop?> TryRewriteReferenceAsync(
        JsonObject parent,
        string path,
        string reference,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        if (!TryParseReference(reference, out var prefix, out var resourceType, out var id, out var suffix))
        {
            return null;
        }

        var redaction = await GetIdRedactionAsync(profileId, resourceType, cancellationToken);
        if (redaction is not { } rule || rule.Strategy == DeIdentificationStrategy.Remove)
        {
            return null;
        }

        var rewritten = prefix + resourceType + "/" + TransformScalar(id, rule.Strategy, rule.ConfigJson) + suffix;
        if (string.Equals(rewritten, reference, StringComparison.Ordinal))
        {
            return null;
        }

        parent["reference"] = rewritten;

        return new DeIdentificationFieldHop(
            path,
            rule.Strategy + ":Reference",
            rule.ConfigJson,
            JsonSerializer.Serialize(reference),
            JsonSerializer.Serialize(rewritten),
            true,
            null);
    }

    /// <summary>
    /// The rule this profile applies to <paramref name="resourceType"/>'s own <c>id</c>, or null when it redacts
    /// no id for that type. Memoized for the lifetime of this scoped instance: a batch commonly holds hundreds of
    /// resources pointing at a handful of types, and rules cannot change mid-run.
    /// </summary>
    private async Task<IdRedaction?> GetIdRedactionAsync(Guid profileId, string resourceType, CancellationToken cancellationToken)
    {
        var key = (profileId, resourceType);
        if (_idRedactionCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        IdRedaction? resolved = null;
        var rules = await _ruleRepository.GetPreMappingRulesAsync(profileId, resourceType, cancellationToken);
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.SourceField) || !TryReadStrategy(rule.ConfigJson, out var strategy))
            {
                continue;
            }

            var segments = ParseSourceFieldPath(rule.SourceField, resourceType);
            if (segments is ["id"])
            {
                resolved = new IdRedaction(strategy, rule.ConfigJson);
                break;
            }
        }

        _idRedactionCache[key] = resolved;
        return resolved;
    }

    /// <summary>
    /// Splits a FHIR reference into the pieces needed to rebuild it with a redacted id. Handles the relative
    /// form ("Patient/123"), the absolute form ("http://host/fhir/Patient/123" — <paramref name="prefix"/> keeps
    /// everything before the type), and a trailing version ("Patient/123/_history/2" — kept in
    /// <paramref name="suffix"/>). Returns false for the forms that carry no resource type and so cannot be
    /// rewritten: contained references ("#obs-1") and urn identifiers ("urn:uuid:…", "urn:oid:…").
    /// </summary>
    internal static bool TryParseReference(
        string reference, out string prefix, out string resourceType, out string id, out string suffix)
    {
        prefix = resourceType = id = suffix = string.Empty;

        var trimmed = reference.Trim();
        if (trimmed.Length == 0
            || trimmed[0] == '#'
            || trimmed.StartsWith("urn:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = trimmed.Split('/');

        // "…/Type/id" normally, or "…/Type/id/_history/v" for a version-specific reference.
        var historyIndex = Array.FindIndex(segments, s => s.Equals("_history", StringComparison.OrdinalIgnoreCase));
        var idIndex = historyIndex > 0 ? historyIndex - 1 : segments.Length - 1;
        var typeIndex = idIndex - 1;
        if (typeIndex < 0 || idIndex >= segments.Length)
        {
            return false;
        }

        var candidateType = segments[typeIndex];
        if (!IsResourceTypeName(candidateType) || segments[idIndex].Length == 0)
        {
            return false;
        }

        prefix = typeIndex == 0 ? string.Empty : string.Join('/', segments[..typeIndex]) + "/";
        resourceType = candidateType;
        id = segments[idIndex];
        suffix = idIndex + 1 >= segments.Length ? string.Empty : "/" + string.Join('/', segments[(idIndex + 1)..]);
        return true;
    }

    /// <summary>A FHIR resource type name is PascalCase ASCII letters — enough to tell "Patient" in
    /// "Patient/123" from a stray path segment in an absolute URL.</summary>
    private static bool IsResourceTypeName(string value)
        => value.Length > 0 && char.IsAsciiLetterUpper(value[0]) && value.All(char.IsAsciiLetter);

    private readonly record struct IdRedaction(DeIdentificationStrategy Strategy, string ConfigJson);

    /// <summary>
    /// Splits a rule's <c>SourceField</c> into the plain property-name segments <see cref="ApplyPath"/> walks,
    /// tolerating the three conventions rules are actually authored in. The seeded defaults use a bare dotted
    /// path ("address.line"); the de-identification tab's field picker writes JsonPath ("$.id", from the payload
    /// tree's <c>jsonPath</c>); and the transformation-rule screens use the "ResourceType.field" convention
    /// ("Patient.id" — the same prefix <c>StripResourceTypePrefix</c> removes for FhirResource-phase rules).
    ///
    /// Normalizing here rather than at the point of authoring is deliberate: it repairs rules already stored in
    /// the database, and it fails safe. A "$" or "Patient" segment matches no property on a FHIR resource, so
    /// before this the whole rule was skipped by the <c>beforeValue is null</c> guard below — no redaction, no
    /// lineage hop, no warning. PHI reached the destination unredacted while the UI showed an active rule.
    ///
    /// Array indexes ("address[0].line") and JsonPath wildcards ("address[*].line") are reduced to the bare
    /// property name, which is what the traversal already does anyway: it fans out across every element of an
    /// array it meets, so redaction applies to all of them rather than one.
    /// </summary>
    internal static string[] ParseSourceFieldPath(string sourceField, string resourceType)
    {
        var path = sourceField.Trim();

        if (path.StartsWith("$.", StringComparison.Ordinal))
        {
            path = path[2..];
        }
        else if (path.StartsWith("$", StringComparison.Ordinal))
        {
            path = path[1..];
        }

        var resourcePrefix = resourceType + ".";
        if (path.StartsWith(resourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = path[resourcePrefix.Length..];
        }

        return path
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(StripIndexer)
            .Where(segment => segment.Length > 0)
            .ToArray();
    }

    /// <summary>Reduces "address[0]" / "address[*]" to "address" — see <see cref="ParseSourceFieldPath"/>.</summary>
    private static string StripIndexer(string segment)
    {
        var bracket = segment.IndexOf('[', StringComparison.Ordinal);
        return bracket < 0 ? segment : segment[..bracket].TrimEnd();
    }

    /// <summary>Best-effort read of the same path <see cref="ApplyPath"/> would mutate — used only to capture a
    /// lineage hop's before/after value, so it deliberately mirrors ApplyPath's array-fan-out/object-descent
    /// rules but returns a single serialized snapshot (the first match) rather than mutating every array element.</summary>
    private static string? TryReadValueAt(JsonNode? node, IReadOnlyList<string> path, int index)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var element in array)
                {
                    var value = TryReadValueAt(element, path, index);
                    if (value is not null)
                    {
                        return value;
                    }
                }

                return null;

            case JsonObject obj when index == path.Count - 1:
                return obj.TryGetPropertyValue(path[index], out var leaf) && leaf is not null
                    ? leaf.ToJsonString()
                    : null;

            case JsonObject obj:
                return obj.TryGetPropertyValue(path[index], out var child)
                    ? TryReadValueAt(child, path, index + 1)
                    : null;

            default:
                return null;
        }
    }

    private static bool TryReadStrategy(string configJson, out DeIdentificationStrategy strategy)
    {
        strategy = default;
        try
        {
            using var document = JsonDocument.Parse(configJson);
            if (!document.RootElement.TryGetProperty("mode", out var modeElement) || modeElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            return Enum.TryParse(modeElement.GetString(), true, out strategy);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ApplyPath(JsonNode? node, IReadOnlyList<string> path, int index, DeIdentificationStrategy strategy, string configJson)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var element in array)
                {
                    ApplyPath(element, path, index, strategy, configJson);
                }

                break;

            case JsonObject obj when index == path.Count - 1:
                ApplyStrategy(obj, path[index], strategy, configJson);
                break;

            case JsonObject obj:
                if (obj.TryGetPropertyValue(path[index], out var child))
                {
                    ApplyPath(child, path, index + 1, strategy, configJson);
                }

                break;
        }
    }

    private static void ApplyStrategy(JsonObject parent, string property, DeIdentificationStrategy strategy, string configJson)
    {
        if (!parent.TryGetPropertyValue(property, out var current) || current is null)
        {
            return;
        }

        if (strategy == DeIdentificationStrategy.Remove)
        {
            parent.Remove(property);
            return;
        }

        // FHIR repeats primitives everywhere — name.given, address.line, and every other 0..* string element —
        // so the value at a rule's path is often an ARRAY of strings rather than one string. Every element is a
        // string with a perfectly good masked/hashed form, but the scalar-only check further down matched only
        // JsonValue and skipped the whole node: a mask rule on "$.name[*].given[*]" did nothing at all while the
        // sibling rule on "$.name[*].family" worked, so unredacted given names and street lines reached the
        // destination under a policy the UI showed as active — the silent failure de-identification can least
        // afford.
        //
        // Only arrays that actually carry strings are handled here. An array of OBJECTS (name, identifier, ...)
        // falls through to the behaviour below, where Redact still removes it wholesale rather than quietly
        // leaving the objects in place.
        if (current is JsonArray array && array.Any(element => element is JsonValue v && v.TryGetValue<string>(out _)))
        {
            // A MIXED array — some strings, some not — is the one shape this branch must not half-handle under
            // Redact. Transforming the strings and returning would leave every non-string element in place, and
            // an object element carries exactly the identifying text the rule exists to remove: ["Camila",
            // {"text":"Camila Maria"}] would write the token over the first and pass the second through, under a
            // policy the UI reports as active. Before arrays were handled at all, Redact removed the whole
            // property here, so half-handling it would be a straight regression on the one strategy whose
            // contract is "this value is gone". Fall through to that wholesale removal instead.
            //
            // Mask and Hash deliberately do NOT fall through: they have no meaningful form for a non-string (the
            // same reason the scalar path below leaves numbers, bools and objects alone), and deleting data under
            // a "mask" rule would be a bigger surprise than leaving it. They transform every string element and
            // leave the rest — strictly more than the nothing-at-all they did before this branch existed.
            var hasNonString = array.Any(element => element is not JsonValue v || !v.TryGetValue<string>(out _));
            if (!(hasNonString && strategy == DeIdentificationStrategy.Redact))
            {
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue element && element.TryGetValue<string>(out var elementRaw))
                    {
                        array[i] = TransformScalar(elementRaw, strategy, configJson);
                    }
                }

                return;
            }
        }

        if (strategy == DeIdentificationStrategy.Redact)
        {
            // A replacement token is a STRING, so it can only stand in for a string. Writing "[REDACTED]" over
            // a boolean or a number produces a value the rest of the pipeline cannot carry: the destination
            // column it maps to is typed (Patient.active -> a bit column), so every row of that resource fails
            // to insert — and because the writer isolates per-record failures rather than throwing, the whole
            // resource silently lands nothing. It is invalid FHIR too: a FHIR-native destination rejects
            // "active": "[REDACTED]" outright.
            //
            // Removing the property instead keeps the intent (the value is gone) and is type-safe everywhere:
            // the mapped column simply comes through null. Strings are unaffected and still get the token.
            if (current is JsonValue redactValue && redactValue.TryGetValue<string>(out _))
            {
                parent[property] = TransformScalar(string.Empty, strategy, configJson);
            }
            else
            {
                parent.Remove(property);
            }

            return;
        }

        // Every remaining strategy rewrites a string in place. A number, bool, object or array-of-objects has no
        // meaningful masked/hashed form, so it is left exactly as it was — an array of strings was handled above.
        if (current is JsonValue value && value.TryGetValue<string>(out var raw))
        {
            parent[property] = TransformScalar(raw, strategy, configJson);
        }
    }

    /// <summary>
    /// The value a strategy produces for one scalar — the single definition of what "hash"/"mask"/"generalize"
    /// mean, shared by <see cref="ApplyStrategy"/> (redacting a field in place) and
    /// <see cref="RewriteReferencesAsync"/> (redacting the id inside a reference that points at a redacted
    /// resource). Sharing it is what makes a child's reference land on the same value as the parent's own id —
    /// two separate implementations would drift and silently break the link they exist to preserve.
    ///
    /// Returns the input unchanged when a strategy's precondition isn't met (a date shorter than 4 characters,
    /// a ZIP shorter than 3), matching the original in-place behaviour of leaving such values alone.
    /// <see cref="DeIdentificationStrategy.Remove"/> is not handled here: it deletes rather than replaces, so it
    /// has no scalar form and callers must special-case it.
    /// </summary>
    private static string TransformScalar(string raw, DeIdentificationStrategy strategy, string configJson) => strategy switch
    {
        DeIdentificationStrategy.Redact => ReadConfigString(configJson, "token") ?? "[REDACTED]",
        DeIdentificationStrategy.Hash => Hash(raw),
        DeIdentificationStrategy.Mask => Mask(raw, ReadConfigInt(configJson, "keepLength", 4)),
        DeIdentificationStrategy.GeneralizeDateToYear => raw.Length >= 4 ? raw[..4] : raw,
        DeIdentificationStrategy.GeneralizeZip3 => raw.Length >= 3 ? raw[..3] + "00" : raw,
        _ => raw,
    };

    private static string Hash(string value)
        => "anon-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();

    private static string Mask(string value, int keepLength)
        => value.Length <= keepLength ? value : new string('*', value.Length - keepLength) + value[^keepLength..];

    private static string? ReadConfigString(string configJson, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(configJson);
            return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a numeric rule-config value, accepting it as either a JSON number or a JSON string.
    ///
    /// The string case is the normal one, not an edge case: the rule-config form stores every field as a string
    /// (<c>config[key] = String(value)</c>), so a <c>keepLength</c> of 4 is persisted as <c>"4"</c>, and the
    /// PostMapping side has always read these through <c>TransformConfigExtensions.GetInt</c>, which is a plain
    /// <c>int.TryParse</c> over strings.
    ///
    /// The ValueKind guard is load-bearing, not defensive tidying: <see cref="JsonElement.TryGetInt32"/> THROWS
    /// <see cref="InvalidOperationException"/> when the element isn't a number — it returns false only for a
    /// number that doesn't fit — and the catch below covers <see cref="JsonException"/> only. So a masking rule
    /// authored in the UI crashed the whole de-identification node, failing the entire workflow run.
    /// </summary>
    private static int ReadConfigInt(string configJson, string property, int fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(configJson);
            if (!document.RootElement.TryGetProperty(property, out var value))
            {
                return fallback;
            }

            return value.ValueKind switch
            {
                JsonValueKind.Number => value.TryGetInt32(out var parsed) ? parsed : fallback,
                JsonValueKind.String => int.TryParse(value.GetString(), out var parsedText) ? parsedText : fallback,
                _ => fallback,
            };
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}

public enum DeIdentificationStrategy
{
    Remove = 0,
    Redact = 1,
    Hash = 2,
    GeneralizeDateToYear = 3,
    GeneralizeZip3 = 4,
    Mask = 5
}
