using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Transforms;

/// <summary>
/// Applies <see cref="TransformArrayMode"/> around a node call — <see cref="TransformArrayMode.Whole"/> (the
/// historical, still-default behavior) runs the node once against the value as-is; <see
/// cref="TransformArrayMode.PerItem"/> runs it once per element when the value is a real collection,
/// reassembling the results into an array. A single per-item failure fails the whole call — partial per-item
/// output isn't a well-formed result for the rule's own error policy to reason about.
///
/// "A real collection" includes a JSON-array-shaped STRING, which is the form a whole-node mapping's value
/// actually arrives in: <c>ArrayPolicy.StoreJson</c> writes the element as raw JSON text, so a rule on
/// <c>Patient.name</c> was handed <c>[{...},{...}]</c> as one opaque string. PerItem could never see it as a
/// collection, and every scalar-oriented node (HumanNameParsing, DateTimeFormat, …) then parsed the JSON
/// source text as if it were a single value — silently, since nothing about that fails. Only
/// <c>ArrayListOperationsNode</c> coped, via its own private unwrap; this generalises it so the mode means
/// the same thing for every node.
///
/// A JSON array reaching a node that converts one scalar fans out per element even under
/// <see cref="TransformArrayMode.Whole"/>: running such a node once over the array's JSON text is never
/// meaningful, and refusing it instead would NULL every column whose rule was saved as Whole before the mode
/// was chosen per row (or whose "match criteria" selection resolved to more than one element).
/// </summary>
public static class TransformNodeApplier
{
    public static TransformResult ExecuteWithArrayMode(
        ITransformNode node, object? value, IReadOnlyDictionary<string, string> config, string? secret, TransformArrayMode arrayMode)
    {
        if (!TryReadItems(node, value, arrayMode, out var items))
        {
            return node.Execute(Normalize(node, value), config, secret);
        }

        var results = new List<object?>();
        foreach (var item in items)
        {
            var result = node.Execute(Normalize(node, item), config, secret);
            if (!result.Success)
            {
                return result;
            }

            results.Add(result.Value);
        }

        return TransformResult.Ok(results.ToArray());
    }

    /// <summary>Async twin of <see cref="ExecuteWithArrayMode"/> — routes through <see cref="ITransformNode.ExecuteAsync"/>
    /// so a node backed by a real dependency (e.g. a DB-backed terminology lookup) can await it, while every other
    /// node's default <see cref="ITransformNode.ExecuteAsync"/> body just wraps its synchronous <c>Execute</c>.
    /// <paramref name="onItemExecuted"/>, when supplied, is invoked once per element for
    /// <see cref="TransformArrayMode.PerItem"/> (with the per-item input value and its own <see cref="TransformResult"/>)
    /// — without it, a lineage capturer watching only the outer call would see one array in, one array out, and lose
    /// which source element produced which destination element.</summary>
    public static async Task<TransformResult> ExecuteWithArrayModeAsync(
        ITransformNode node, object? value, IReadOnlyDictionary<string, string> config, string? secret,
        TransformArrayMode arrayMode, CancellationToken cancellationToken = default,
        Action<object?, TransformResult>? onItemExecuted = null)
    {
        if (!TryReadItems(node, value, arrayMode, out var items))
        {
            return await node.ExecuteAsync(Normalize(node, value), config, secret, cancellationToken);
        }

        var results = new List<object?>();
        foreach (var item in items)
        {
            var result = await node.ExecuteAsync(Normalize(node, item), config, secret, cancellationToken);
            onItemExecuted?.Invoke(item, result);
            if (!result.Success)
            {
                return result;
            }

            results.Add(result.Value);
        }

        return TransformResult.Ok(results.ToArray());
    }

    /// <summary>
    /// The elements a call should fan out over, or false to run the node once against the value as-is.
    /// A plain string is never a collection of characters, but a string that IS a JSON array is the whole-node
    /// mapping's on-the-wire form and unwraps to its elements. Each element is handed on as a live
    /// <see cref="JsonNode"/> (a JSON string element as its bare text), so a node reading a structured value
    /// sees a real object rather than text it would have to re-parse.
    ///
    /// PerItem fans out over any collection. Whole fans out only over a JSON array handed to a node that has
    /// not declared it reads collections (see the class summary) — a CLR collection, such as the raw repeats a
    /// ConcatenationTemplating chain is handed, still reaches the node whole.
    /// </summary>
    private static bool TryReadItems(
        ITransformNode node, object? value, TransformArrayMode arrayMode, out IEnumerable items)
    {
        items = Array.Empty<object?>();
        if (value is null)
        {
            return false;
        }

        var perItem = arrayMode == TransformArrayMode.PerItem;
        if (!perItem && node.AcceptsCollections)
        {
            return false;
        }

        if (TryReadJsonArray(value, out var jsonItems))
        {
            items = jsonItems;
            return true;
        }

        // A JsonObject is an IEnumerable of its own key/value pairs — one element, never a collection of them.
        if (perItem && value is IEnumerable enumerable and not string and not JsonNode)
        {
            items = enumerable;
            return true;
        }

        return false;
    }

    private static bool TryReadJsonArray(object value, out object?[] items)
    {
        items = [];
        if (value is JsonArray jsonArray)
        {
            items = jsonArray.Select(ToItem).ToArray();
            return true;
        }

        if (value is not string text || !text.TrimStart().StartsWith('['))
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(text) is not JsonArray parsed)
            {
                return false;
            }

            items = parsed.Select(ToItem).ToArray();
            return true;
        }
        catch (JsonException)
        {
            // Not valid JSON after all — a value that merely starts with "[" is left to the node as-is.
            return false;
        }
    }

    private static object? ToItem(JsonNode? node) =>
        node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) ? text : node;

    /// <summary>
    /// Gives a node the same KIND of value whichever mapping fed it, so a rule on a repeating element behaves
    /// like the same rule on one of its fields. A mapping of a whole FHIR element hands over the element;
    /// a mapping of a leaf hands over a scalar. Every node but one converts a scalar — so a structured element
    /// is reduced to its own <c>text</c>, which is FHIR's human-readable rendering of it and exactly what a
    /// mapping of <c>&lt;element&gt;.text</c> would have produced. Without this, AddressParsingNode split an
    /// address element's raw JSON on its commas and wrote `"country":"US"` into the city column.
    ///
    /// A JSON array never reaches here for a node that cannot read one — <see cref="TryReadItems"/> has already
    /// fanned it out.
    /// </summary>
    private static object? Normalize(ITransformNode node, object? value)
    {
        if (value is null || node.AcceptsCollections || node.AcceptsStructuredValue)
        {
            return value;
        }

        // An element with no `text` is left alone: it may be a shape the node genuinely reads (a structured
        // value arriving from an earlier rule in the chain), and inventing a scalar for it would be guesswork.
        return AsJsonNode(value) is JsonObject jsonObject
               && jsonObject["text"] is JsonValue textValue
               && textValue.TryGetValue<string>(out var text)
               && !string.IsNullOrWhiteSpace(text)
            ? text
            : value;
    }

    /// <summary>The value as a JSON node when it is one, or is the text of one — a mapped element arrives as
    /// raw JSON text (ArrayPolicy.StoreJson), an upstream node's output as a live node. Anything else, including
    /// an ordinary string that merely starts with a brace, is not.</summary>
    private static JsonNode? AsJsonNode(object value)
    {
        if (value is JsonNode node)
        {
            return node;
        }

        if (value is not string text)
        {
            return null;
        }

        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
