using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Domain.ValueObjects;

/// <summary>
/// Reads the per-field markers the mapping UI encodes onto <see cref="MappingField.Format"/>.
///
/// <see cref="MappingField.Format"/> has always doubled as this field's column-mode marker bag
/// ("directField", "directField;aggregate=csv", "joinedFields;delimiter=...", "wholeNodeAsJson" — see
/// MappingImportService.BuildJsonPathAndFormat, and JsonMappingEngine's own "aggregate=csv" handling),
/// precisely because these are authoring choices with no matching <see cref="ArrayPolicy"/> value. The
/// MongoDB "store this JSON as a real sub-document, not as text" choice is another one of those, so it
/// rides the same marker channel rather than adding a column to the MappingFields table — which also means
/// every mapping profile saved before the option existed reads back as the default,
/// <see cref="JsonColumnWriteMode.JsonString"/>, with no migration and no behaviour change.
/// </summary>
public static class MappingFieldFormat
{
    /// <summary>Marker selecting <see cref="JsonColumnWriteMode.Document"/>, e.g. "wholeNodeAsJson;json=document".</summary>
    public const string JsonWriteModeDocumentMarker = "json=document";

    /// <summary>Marker selecting <see cref="JsonColumnWriteMode.JsonString"/> explicitly. Same result as
    /// omitting it entirely; written by the UI only so a saved profile records the choice rather than
    /// leaving it implicit.</summary>
    public const string JsonWriteModeStringMarker = "json=string";

    /// <summary>
    /// The JSON write mode this field's <paramref name="format"/> selects. Anything without an explicit
    /// marker — including a null/blank format, which is what every field authored before this option
    /// carries — is <see cref="JsonColumnWriteMode.JsonString"/>.
    ///
    /// Matches whole ';'-separated segments, exactly as <c>JsonMappingEngine.ParseDelimiter</c> reads its own
    /// marker out of this same string — NOT a raw substring search over the whole value. Format is a marker
    /// bag whose segments can carry arbitrary user text: "joinedFields;delimiter=X" takes everything after
    /// the first '=' as the delimiter, so a delimiter of "json=document" would otherwise flip an unrelated
    /// joined-string column into document storage.
    /// </summary>
    public static JsonColumnWriteMode ReadJsonWriteMode(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return JsonColumnWriteMode.JsonString;
        }

        foreach (var segment in format.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (segment.Equals(JsonWriteModeDocumentMarker, StringComparison.OrdinalIgnoreCase))
            {
                return JsonColumnWriteMode.Document;
            }
        }

        return JsonColumnWriteMode.JsonString;
    }
}
