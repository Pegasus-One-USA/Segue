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

/// <summary>12. HumanName Parsing &amp; Formatting.</summary>
public sealed class HumanNameParsingNode : ITransformNode
{
    public TransformNodeType NodeType => TransformNodeType.HumanNameParsing;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var raw = value?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TransformResult.Ok(null);
        }

        var prefixTokens = config.Get("prefixTokens", "Dr,Mr,Mrs,Ms,Miss")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var suffixTokens = config.Get("suffixTokens", "Jr,Sr,II,III,IV,MD,PhD")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        string family;
        List<string> given;
        string? prefix = null;
        string? suffix = null;

        if (config.Get("pattern", "FirstLast") == "LastFirstMiddle" || raw.Contains(','))
        {
            var parts = raw.Split(',', 2, StringSplitOptions.TrimEntries);
            family = parts[0];
            given = parts.Length > 1 ? [.. parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)] : [];
        }
        else
        {
            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
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

            family = parts.Count > 0 ? parts[^1] : raw;
            given = parts.Count > 1 ? parts[..^1] : [];
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
