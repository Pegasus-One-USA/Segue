using System.Text.Json;
using MongoDB.Bson;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Turns the JSON text a <c>ValueType=Json</c> mapping field produces (JsonMappingEngine hands these
/// through as raw JSON — <c>JsonElement.GetRawText()</c>, never re-escaped) into the native BSON value
/// MongoDB stores as a real sub-document, for fields whose mapping selected
/// <see cref="FHIRBridge.Domain.Enums.JsonColumnWriteMode.Document"/>.
///
/// Deliberately parses with <see cref="System.Text.Json"/> and converts element by element rather than
/// calling <c>BsonDocument.Parse</c>: the latter reads MongoDB Extended JSON, where a plain object whose
/// key happens to start with "$" (<c>{"$date": ...}</c>, <c>{"$oid": ...}</c>) is silently reinterpreted
/// as a typed BSON value instead of being stored as the object it is. FHIR payloads carry arbitrary
/// extension content, so that reinterpretation is a data-corruption risk with no upside here.
/// </summary>
public static class MongoJsonValueConverter
{
    /// <summary>
    /// Parses <paramref name="json"/> into its BSON equivalent. Returns false — leaving
    /// <paramref name="value"/> at <see cref="BsonNull.Value"/> — when the text isn't valid JSON or can't be
    /// represented as a BSON document (e.g. duplicate keys), so callers can fall back to storing the
    /// original text, which is exactly the behaviour this option opts out of and therefore always safe.
    /// </summary>
    public static bool TryParse(string json, out BsonValue value)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            value = ToBsonValue(document.RootElement);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            value = BsonNull.Value;
            return false;
        }
    }

    private static BsonValue ToBsonValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => new BsonDocument(
            element.EnumerateObject().Select(property => new BsonElement(property.Name, ToBsonValue(property.Value)))),

        JsonValueKind.Array => new BsonArray(element.EnumerateArray().Select(ToBsonValue)),

        JsonValueKind.String => new BsonString(element.GetString() ?? string.Empty),

        // Integers keep their natural width; everything else becomes Decimal128 rather than a double. FHIR
        // requires a decimal's precision to be preserved exactly (a dose of 0.1 must not come back as
        // 0.1000000000000000055…), and Decimal128 is the only BSON numeric type that does that.
        JsonValueKind.Number =>
            element.TryGetInt32(out var int32) ? new BsonInt32(int32)
            : element.TryGetInt64(out var int64) ? new BsonInt64(int64)
            : element.TryGetDecimal(out var dec) ? new BsonDecimal128(dec)
            : new BsonDouble(element.GetDouble()),

        JsonValueKind.True => BsonBoolean.True,
        JsonValueKind.False => BsonBoolean.False,

        // Null and Undefined — a JSON null is a real, meaningful absence in FHIR; there is no other kind.
        _ => BsonNull.Value,
    };
}
