using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FHIRBridge.Domain.Enums;
using PhoneNumbers;

namespace FHIRBridge.Application.Services.Transforms.Nodes;

/// <summary>10. Reference Construction &amp; Rewriting.</summary>
public sealed class ReferenceConstructionNode : ITransformNode
{
    public TransformNodeType NodeType => TransformNodeType.ReferenceConstruction;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var raw = value?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TransformResult.Ok(null);
        }

        var resourceType = config.Get("resourceType", "Patient");
        var allowedTargetTypes = config.GetOrNull("allowedTargetTypes")?
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (allowedTargetTypes is { Length: > 0 } && !allowedTargetTypes.Contains(resourceType, StringComparer.OrdinalIgnoreCase))
        {
            return TransformResult.Fail($"'{resourceType}' is not one of the allowed target types ({string.Join(", ", allowedTargetTypes)}).");
        }

        var style = config.Get("style", "relative");
        var id = raw.Contains('/') ? raw[(raw.LastIndexOf('/') + 1)..] : raw;

        if (style == "logical")
        {
            var identifierSystem = config.Get("identifierSystem");
            var logicalResult = new JsonObject
            {
                ["identifier"] = new JsonObject { ["system"] = identifierSystem, ["value"] = id }
            };
            var logicalDisplay = config.GetOrNull("display");
            if (logicalDisplay is not null)
            {
                logicalResult["display"] = logicalDisplay;
            }

            return TransformResult.Ok(logicalResult);
        }

        var reference = style switch
        {
            "urn" => $"urn:uuid:{id}",
            "absolute" => $"{config.Get("baseUrl").TrimEnd('/')}/{resourceType}/{id}",
            _ => $"{resourceType}/{id}"
        };

        var result = new JsonObject { ["reference"] = reference };
        var display = config.GetOrNull("display");
        if (display is not null)
        {
            result["display"] = display;
        }

        return TransformResult.Ok(result);
    }
}

/// <summary>11. Identifier Formatting &amp; Normalization.</summary>
public sealed class IdentifierFormattingNode : ITransformNode
{
    private static readonly Regex NonAlphaNumeric = new(@"[^A-Za-z0-9]", RegexOptions.Compiled);

    public TransformNodeType NodeType => TransformNodeType.IdentifierFormatting;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var raw = value?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TransformResult.Ok(null);
        }

        var cleaned = NonAlphaNumeric.Replace(raw.Trim(), string.Empty);
        var padLength = config.GetInt("padLength", 0);
        if (padLength > 0)
        {
            cleaned = cleaned.PadLeft(padLength, '0');
        }

        var typeCode = config.GetOrNull("typeCode");
        if (typeCode == "NPI" && !IsValidNpiCheckDigit(cleaned))
        {
            return TransformResult.Fail($"'{cleaned}' failed the NPI Luhn check digit.");
        }

        var identifier = new JsonObject { ["system"] = config.Get("system"), ["value"] = cleaned };
        if (typeCode is not null)
        {
            identifier["type"] = typeCode;
        }

        return TransformResult.Ok(identifier);
    }

    private static bool IsValidNpiCheckDigit(string npi)
    {
        if (npi.Length != 10 || !npi.All(char.IsDigit))
        {
            return false;
        }

        // NPI Luhn check over "80840" + first 9 digits, validated against the 10th (checksum) digit.
        var digits = ("80840" + npi[..9]).Reverse().Select(c => c - '0').ToArray();
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[i];
            if (i % 2 == 0)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
        }

        var checkDigit = (10 - sum % 10) % 10;
        return checkDigit == npi[9] - '0';
    }
}

/// <summary>12. HumanName Parsing &amp; Formatting — bidirectional.
/// <para>String → HumanName: recognized prefix/suffix tokens are stripped first (consistently, in every
/// pattern), then the remaining tokens are assigned by the configured positional <c>pattern</c>.</para>
/// <para>HumanName → string: when the incoming value is already a structured name (a JsonObject, or JSON
/// object text), the node composes a display string from the configured <c>format</c> token order instead
/// of parsing. That makes the node round-trippable without a second node type: the direction follows the
/// shape of the input, so a chain that parses then formats needs no extra configuration.</para></summary>
public sealed class HumanNameParsingNode : ITransformNode
{
    /// <summary>The <c>format</c> tokens recognized when composing a display string, lower-cased for lookup.
    /// "Middle" maps to the given names after the first, which is where a parsed middle name lands.</summary>
    private static readonly string[] FormatTokens = ["prefix", "first", "middle", "given", "last", "family", "suffix"];

    /// <summary>The <c>pattern</c> value selecting the one-rule parse-then-format mode.</summary>
    private const string RoundTripPattern = "RoundTrip";

    public TransformNodeType NodeType => TransformNodeType.HumanNameParsing;

    /// <summary>Reads the element rather than only its display string: <c>use</c> says which of a patient's
    /// names this is, and no rendering of the name carries it. An element with no <c>text</c> is also the one
    /// shape this node composes FROM, rather than parses.</summary>
    public bool AcceptsStructuredValue => true;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        if (value is null)
        {
            return TransformResult.Ok(null);
        }

        // Two independent halves, because they answer different questions and a single "pattern" could only
        // ever answer one of them: FIRST resolve the name's parts (from an already-structured element, or by
        // parsing a display string with `pattern`), THEN emit either those parts or a display string composed
        // in the token order `format` names. Blank `format` means "emit the parts", which is why it is the
        // default — a rule that names no output shape should not silently flatten a name into a string.
        var resolved = ResolveName(value, config);
        if (!resolved.Success || resolved.Value is not JsonObject name)
        {
            return resolved;
        }

        var format = config.GetOrNull("format");

        // RoundTrip predates `format` being reachable on its own and has always meant parse-then-compose, so
        // it still composes even when no format is named — with the shape it has always defaulted to.
        if (string.IsNullOrWhiteSpace(format) && config.Get("pattern", "FirstLast") == RoundTripPattern)
        {
            format = "First Middle Last Suffix";
        }

        return string.IsNullOrWhiteSpace(format)
            ? TransformResult.Ok(name)
            : Format(name, new Dictionary<string, string>(config, StringComparer.OrdinalIgnoreCase) { ["format"] = format });
    }

    /// <summary>
    /// The name's parts, however they have to be arrived at. An already-structured element is authoritative
    /// for the parts it states — the source system parsed its own data, and re-deriving family/given from the
    /// display string instead discards that and makes the result depend on the author picking a `pattern` that
    /// happens to match this record's name order. Its display string is still read for the affixes, which FHIR
    /// routinely leaves only in `text`: a "III" or a "Dr" that the element itself never states.
    /// </summary>
    private static TransformResult ResolveName(object value, IReadOnlyDictionary<string, string> config)
    {
        // TryReadStructuredName also accepts JSON object *text*, because an upstream node's JsonObject is
        // serialized to its JSON string on the way to a destination field (see TransformNodeExecutors), so a
        // re-read structured name arrives here as text rather than as a live node.
        if (TryReadStructuredName(value, out var structured))
        {
            // An element already states its own parts, so nothing is parsed out of the display string — the
            // element IS the parse. `pattern` therefore selects the order those parts are composed in, and the
            // result is a display string. See PatternFormats for the five orders.
            //
            // The display string is still read for the affixes, which FHIR routinely leaves only in `text`:
            // "Warren James McGinnis III" carries a suffix the element itself never states, and RoundTrip is
            // defined to include it.
            var name = EnrichAffixesFromText(structured!, ReadString(structured["text"]), config);
            var format = config.GetOrNull("format") is { Length: > 0 } explicitFormat
                ? explicitFormat
                : PatternFormats.GetValueOrDefault(config.Get("pattern", "FirstLast"), "First Last");

            return Format(name, new Dictionary<string, string>(config, StringComparer.OrdinalIgnoreCase)
            {
                ["format"] = format,
            });
        }

        var raw = value.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TransformResult.Ok(null);
        }

        // `roundTripPattern` names the order to read the string in when `pattern` is spent selecting RoundTrip.
        var parseConfig = config.Get("pattern", "FirstLast") == RoundTripPattern
            ? new Dictionary<string, string>(config, StringComparer.OrdinalIgnoreCase)
            {
                ["pattern"] = config.Get("roundTripPattern", "FirstLast"),
            }
            : config;

        return Parse(raw, parseConfig);
    }

    /// <summary>
    /// What each mode composes from an element's own parts. "First" is the first given name, "Middle" the
    /// given names after it, "Given" all of them, "Last"/"Family" the family name.
    ///   FirstLast        -> Warren McGinnis
    ///   FirstMiddleLast  -> Warren James McGinnis
    ///   FirstLastMiddle  -> Warren McGinnis James
    ///   LastFirstMiddle  -> McGinnis Warren James
    ///   RoundTrip        -> Warren James McGinnis III   (prefix, given, family, suffix)
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> PatternFormats =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FirstLast"] = "First Last",
            ["FirstMiddleLast"] = "First Middle Last",
            ["FirstLastMiddle"] = "First Last Middle",
            ["LastFirstMiddle"] = "Last First Middle",
            [RoundTripPattern] = "Prefix Given Family Suffix",
        };

    /// <summary>
    /// The element's own parts, plus only the affixes it is missing, read out of its display string. Never
    /// overwrites a prefix/suffix the element already states.
    /// </summary>
    private static JsonObject EnrichAffixesFromText(
        JsonObject name, string? text, IReadOnlyDictionary<string, string> config)
    {
        var enriched = new JsonObject
        {
            ["family"] = ReadString(name["family"]),
            ["given"] = new JsonArray(ReadStrings(name["given"]).Select(g => (JsonNode)g).ToArray()),
        };

        var parsed = string.IsNullOrWhiteSpace(text) ? null : Parse(text, config).Value as JsonObject;

        foreach (var affix in new[] { "prefix", "suffix" })
        {
            var own = ReadStrings(name[affix]);
            var fromText = parsed is null ? [] : ReadStrings(parsed[affix]);
            var values = own.Count > 0 ? own : fromText;
            if (values.Count > 0)
            {
                enriched[affix] = new JsonArray(values.Select(v => (JsonNode)v).ToArray());
            }
        }

        return enriched;
    }

    /// <summary>Parses an element's display string, keeping the element's own <c>use</c> — nothing else can
    /// supply it, and without it a per-item run over Patient.name loses which name was the official one. An
    /// explicit config <c>use</c> still wins.</summary>
    private static TransformResult ParseCarryingElementUse(
        string text, JsonObject element, IReadOnlyDictionary<string, string> config)
    {
        var parsed = Parse(text, config);
        if (parsed.Success && parsed.Value is JsonObject parsedName
            && config.GetOrNull("use") is null && ReadString(element["use"]) is { Length: > 0 } elementUse)
        {
            parsedName["use"] = elementUse;
        }

        return parsed;
    }

    private static TransformResult Parse(string raw, IReadOnlyDictionary<string, string> config)
    {
        var prefixTokens = config.Get("prefixTokens", "Dr,Mr,Mrs,Ms,Miss")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var suffixTokens = config.Get("suffixTokens", "Jr,Sr,II,III,IV,MD,PhD")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        var pattern = config.Get("pattern", "FirstLast");

        string family;
        List<string> given;
        string? prefix = null;
        string? suffix = null;

        // "Last, First Middle" is self-describing: the comma states the order outright, so it still wins over
        // a positional pattern that assumed there would not be one.
        if (pattern == "LastFirstMiddle" || raw.Contains(','))
        {
            var parts = raw.Split(',', 2, StringSplitOptions.TrimEntries);
            family = parts[0];
            given = parts.Length > 1 ? [.. parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)] : [];
        }
        else
        {
            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

            // Affixes come off before any positional assignment, and in every pattern — otherwise a trailing
            // "III" occupies a name position and shifts every remaining token by one.
            if (parts.Count > 0 && prefixTokens.Contains(parts[0].TrimEnd('.'), StringComparer.OrdinalIgnoreCase))
            {
                prefix = parts[0];
                parts.RemoveAt(0);
            }

            if (parts.Count > 0 && suffixTokens.Contains(parts[^1].TrimEnd('.'), StringComparer.OrdinalIgnoreCase))
            {
                suffix = parts[^1];
                parts.RemoveAt(parts.Count - 1);
            }

            if (pattern == "FirstMiddleLast" && parts.Count > 1)
            {
                // Positional: the LAST token is the surname and everything before it is a given name — the
                // ordinary Western reading of "Warren James McGinnis". Distinct from FirstLast, which treats
                // everything after the first token as one multi-word surname ("James McGinnis") and so cannot
                // express a middle name at all.
                family = parts[^1];
                given = [.. parts[..^1]];
            }
            else if (pattern == "FirstLastMiddle" && parts.Count > 1)
            {
                // Positional: token 1 = first (given), token 2 = last (family), tokens 3+ = middle (given).
                // A source that emits this order puts the surname in the *second* position, so the trailing
                // tokens are further given names rather than a continuation of the family name.
                family = parts[1];
                given = [parts[0]];
                if (parts.Count > 2)
                {
                    given.AddRange(parts[2..]);
                }
            }
            else
            {
                // FirstLast: the first token is the given name and everything after it is the family name, so
                // a multi-word surname ("James McGinnis") survives intact instead of being split at the space.
                // A lone token is a surname, not a given name — the same fallback FirstLastMiddle drops to.
                family = parts.Count > 1 ? string.Join(' ', parts[1..]) : parts.Count > 0 ? parts[0] : raw;
                given = parts.Count > 1 ? [parts[0]] : [];
            }
        }

        var name = new JsonObject { ["family"] = family, ["given"] = new JsonArray(given.Select(g => (JsonNode)g).ToArray()) };
        if (prefix is not null)
        {
            name["prefix"] = new JsonArray((JsonNode)prefix);
        }

        if (suffix is not null)
        {
            name["suffix"] = new JsonArray((JsonNode)suffix);
        }

        var use = config.GetOrNull("use");
        if (use is not null)
        {
            name["use"] = use;
        }

        if (config.GetBool("setText", true))
        {
            name["text"] = raw;
        }

        return TransformResult.Ok(name);
    }

    /// <summary>Composes a display string from a structured name, in the token order given by <c>format</c>
    /// (e.g. "First Middle Last Suffix"). A token the name has no value for is dropped rather than leaving a
    /// double space behind, so a name with no middle name or suffix still formats cleanly.</summary>
    private static TransformResult Format(JsonObject name, IReadOnlyDictionary<string, string> config)
    {
        var format = config.Get("format", "First Middle Last Suffix");
        var tokens = format.Split([' ', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return TransformResult.Fail("'format' must name at least one HumanName part (e.g. \"First Middle Last Suffix\").");
        }

        var unknown = tokens.FirstOrDefault(t => !FormatTokens.Contains(t.ToLowerInvariant()));
        if (unknown is not null)
        {
            return TransformResult.Fail($"'{unknown}' is not a recognized name part — use any of: {string.Join(", ", FormatTokens)}.");
        }

        var givenNames = ReadStrings(name["given"]);
        var pieces = new List<string>();
        foreach (var token in tokens)
        {
            var piece = token.ToLowerInvariant() switch
            {
                "prefix" => string.Join(' ', ReadStrings(name["prefix"])),
                "first" => givenNames.Count > 0 ? givenNames[0] : string.Empty,
                "middle" => givenNames.Count > 1 ? string.Join(' ', givenNames[1..]) : string.Empty,
                "given" => string.Join(' ', givenNames),
                "last" or "family" => ReadString(name["family"]),
                _ => string.Join(' ', ReadStrings(name["suffix"])),
            };

            if (!string.IsNullOrWhiteSpace(piece))
            {
                pieces.Add(piece.Trim());
            }
        }

        return TransformResult.Ok(string.Join(' ', pieces));
    }

    /// <summary>Recognizes an already-structured HumanName: a live JsonObject from an upstream node, or the
    /// JSON object text one was serialized to. Anything else — a plain display string, a number — is not a
    /// structured name and is left to the parsing direction.</summary>
    private static bool TryReadStructuredName(object value, out JsonObject? name)
    {
        name = null;
        if (value is JsonObject jsonObject)
        {
            name = jsonObject;
            return true;
        }

        // Only a string can carry JSON object text; a JsonValue/JsonArray or any other CLR value cannot.
        if (value is not string text || !text.TrimStart().StartsWith('{'))
        {
            return false;
        }

        try
        {
            name = JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        return name is not null;
    }

    private static string ReadString(JsonNode? node) => node is null ? string.Empty : node.ToString();

    /// <summary>Reads a HumanName string-array part (given/prefix/suffix), tolerating a bare string where the
    /// array is expected — hand-authored and round-tripped payloads both turn up in practice.</summary>
    private static List<string> ReadStrings(JsonNode? node) => node switch
    {
        JsonArray array => [.. array.Where(n => n is not null).Select(n => n!.ToString()).Where(s => !string.IsNullOrWhiteSpace(s))],
        JsonValue value => string.IsNullOrWhiteSpace(value.ToString()) ? [] : [value.ToString()],
        _ => [],
    };
}

/// <summary>13. Address Parsing &amp; Normalization — a comma/newline-delimited parser
/// ("line[, line2, ...], city, state postalCode") with US state and country full-name normalization;
/// full third-party address-parsing libraries are out of scope here.</summary>
public sealed class AddressParsingNode : ITransformNode
{
    private static readonly Regex StateZip = new(@"^([A-Za-z .]{2,})\s+(\d{5}(-\d{4})?)$", RegexOptions.Compiled);

    // Epic's address.text renders "city state zip" as ONE comma segment (no comma between city and
    // state, unlike the classic "line, city, state zip" shape StateZip above expects) followed by a
    // separate country segment — e.g. "134 Elm St\r\nMadison WI 53706\r\nUnited States of America".
    // Non-greedy city capture so "New York NY 10001" still splits city="New York" rather than the
    // state token swallowing part of the city name.
    private static readonly Regex CityStateZip = new(@"^(.+?)\s+([A-Za-z .]{2,})\s+(\d{5}(-\d{4})?)$", RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, string> UsStateAbbreviations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["alabama"] = "AL", ["alaska"] = "AK", ["arizona"] = "AZ", ["arkansas"] = "AR", ["california"] = "CA",
        ["colorado"] = "CO", ["connecticut"] = "CT", ["delaware"] = "DE", ["district of columbia"] = "DC",
        ["florida"] = "FL", ["georgia"] = "GA", ["hawaii"] = "HI", ["idaho"] = "ID", ["illinois"] = "IL",
        ["indiana"] = "IN", ["iowa"] = "IA", ["kansas"] = "KS", ["kentucky"] = "KY", ["louisiana"] = "LA",
        ["maine"] = "ME", ["maryland"] = "MD", ["massachusetts"] = "MA", ["michigan"] = "MI",
        ["minnesota"] = "MN", ["mississippi"] = "MS", ["missouri"] = "MO", ["montana"] = "MT",
        ["nebraska"] = "NE", ["nevada"] = "NV", ["new hampshire"] = "NH", ["new jersey"] = "NJ",
        ["new mexico"] = "NM", ["new york"] = "NY", ["north carolina"] = "NC", ["north dakota"] = "ND",
        ["ohio"] = "OH", ["oklahoma"] = "OK", ["oregon"] = "OR", ["pennsylvania"] = "PA",
        ["rhode island"] = "RI", ["south carolina"] = "SC", ["south dakota"] = "SD", ["tennessee"] = "TN",
        ["texas"] = "TX", ["utah"] = "UT", ["vermont"] = "VT", ["virginia"] = "VA", ["washington"] = "WA",
        ["west virginia"] = "WV", ["wisconsin"] = "WI", ["wyoming"] = "WY"
    };

    private static readonly IReadOnlyDictionary<string, string> CountryIso3166 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["united states"] = "US", ["united states of america"] = "US", ["usa"] = "US",
        ["canada"] = "CA", ["united kingdom"] = "GB", ["mexico"] = "MX"
    };

    public TransformNodeType NodeType => TransformNodeType.AddressParsing;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var raw = value?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TransformResult.Ok(null);
        }

        // A second Apt/Suite line arrives either newline-separated or as an extra comma segment — normalize
        // both to the same comma-delimited form before splitting.
        var parts = raw.Replace("\r\n", "\n").Replace('\n', ',')
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        var address = new JsonObject();
        var countryFromText = false;

        if (parts.Length >= 3)
        {
            // Everything before the trailing two segments is a street line — supports a second
            // Apt/Suite line instead of assuming exactly one line always precedes city.
            address["line"] = new JsonArray(parts[..^2].Select(l => (JsonNode)l).ToArray());

            if (CityStateZip.IsMatch(parts[^2]))
            {
                // Epic shape — "line, city state zip, country": the second-to-last segment already
                // contains an embedded "ST 12345" (a zip glommed onto the city, no comma between them),
                // which the classic shape below never has on its (zip-free) city segment. Treat it as
                // "city state zip" combined, and the trailing segment as a country name.
                ApplyCityStateZip(address, parts[^2]);
                var countryText = parts[^1];
                address["country"] = CountryIso3166.TryGetValue(countryText, out var isoCountryFromText)
                    ? isoCountryFromText : countryText;
                countryFromText = true;
            }
            else
            {
                // Classic "line, city, state zip" shape — city and state+zip are separate segments.
                address["city"] = parts[^2];
                ApplyStateZip(address, parts[^1]);
            }
        }
        else
        {
            address["line"] = new JsonArray(parts.Length > 0 ? (JsonNode)parts[0] : "");
            if (parts.Length > 1)
            {
                address["city"] = parts[1];
            }
        }

        address["use"] = config.Get("use", "home");
        var type = config.GetOrNull("type");
        if (type is not null)
        {
            address["type"] = type;
        }

        if (!countryFromText)
        {
            var country = config.Get("country", "US");
            address["country"] = CountryIso3166.TryGetValue(country, out var isoCountry) ? isoCountry : country;
        }

        return TransformResult.Ok(address);
    }

    /// <summary>Splits a single "City ST ZIP" segment (no comma between city and state) into its three
    /// parts — the Epic-shape counterpart to <see cref="ApplyStateZip"/>, which expects state+zip
    /// already isolated on its own segment. Falls back to treating the whole segment as the city (no
    /// state/postalCode) when it doesn't match the expected trailing "ST 12345" pattern at all.</summary>
    private static void ApplyCityStateZip(JsonObject address, string cityStateZip)
    {
        var match = CityStateZip.Match(cityStateZip);
        if (!match.Success)
        {
            address["city"] = cityStateZip;
            return;
        }

        address["city"] = match.Groups[1].Value.Trim();
        address["state"] = NormalizeState(match.Groups[2].Value.Trim());
        address["postalCode"] = match.Groups[3].Value;
    }

    private static void ApplyStateZip(JsonObject address, string stateZip)
    {
        var match = StateZip.Match(stateZip);
        var stateToken = match.Success ? match.Groups[1].Value.Trim() : stateZip;
        address["state"] = NormalizeState(stateToken);
        if (match.Success)
        {
            address["postalCode"] = match.Groups[2].Value;
        }
    }

    private static string NormalizeState(string state) =>
        state.Length == 2 ? state.ToUpperInvariant() :
        UsStateAbbreviations.TryGetValue(state, out var abbr) ? abbr : state;
}

/// <summary>14. Telecom (ContactPoint) Normalization — phone numbers are parsed and formatted to E.164 via
/// libphonenumber (Google's library, region-aware rather than guessing off digit count).</summary>
public sealed class TelecomNormalizationNode : ITransformNode
{
    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
    private static readonly PhoneNumberUtil PhoneUtil = PhoneNumberUtil.GetInstance();

    public TransformNodeType NodeType => TransformNodeType.TelecomNormalization;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var raw = value?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TransformResult.Ok(null);
        }

        var rank = config.GetOrNull("rank");

        if (EmailPattern.IsMatch(raw))
        {
            var email = new JsonObject { ["system"] = "email", ["value"] = raw, ["use"] = config.Get("use", "home") };
            if (rank is not null)
            {
                email["rank"] = int.TryParse(rank, out var emailRank) ? emailRank : null;
            }

            return TransformResult.Ok(email);
        }

        // "system" config can explicitly force fax/url instead of the phone-vs-email auto-detection above —
        // there's no reliable free-text signal to detect fax/url from the raw value itself.
        var explicitSystem = config.GetOrNull("system");
        if (explicitSystem is "fax" or "url")
        {
            var result = new JsonObject { ["system"] = explicitSystem, ["value"] = raw, ["use"] = config.Get("use", "work") };
            if (rank is not null)
            {
                result["rank"] = int.TryParse(rank, out var explicitRank) ? explicitRank : null;
            }

            return TransformResult.Ok(result);
        }

        var region = config.Get("region", "US");
        string normalized;
        try
        {
            var parsedNumber = PhoneUtil.Parse(raw, region);
            if (!PhoneUtil.IsValidNumber(parsedNumber))
            {
                return TransformResult.Fail($"'{raw}' is not a valid phone number for region '{region}'.");
            }

            normalized = PhoneUtil.Format(parsedNumber, PhoneNumberFormat.E164);
        }
        catch (NumberParseException ex)
        {
            return TransformResult.Fail($"Unable to parse '{raw}' as a phone number: {ex.Message}");
        }

        var phone = new JsonObject { ["system"] = "phone", ["value"] = normalized, ["use"] = config.Get("use", "mobile") };
        if (rank is not null)
        {
            phone["rank"] = int.TryParse(rank, out var phoneRank) ? phoneRank : null;
        }

        return TransformResult.Ok(phone);
    }
}
