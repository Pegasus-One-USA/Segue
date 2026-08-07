using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FHIRBridge.Domain.Enums;

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
        var style = config.Get("style", "relative");
        var id = raw.Contains('/') ? raw[(raw.LastIndexOf('/') + 1)..] : raw;

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

        string family;
        string[] given;

        if (config.Get("pattern", "FirstLast") == "LastFirstMiddle" || raw.Contains(','))
        {
            var parts = raw.Split(',', 2, StringSplitOptions.TrimEntries);
            family = parts[0];
            given = parts.Length > 1 ? parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
        }
        else
        {
            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            family = parts.Length > 0 ? parts[^1] : raw;
            given = parts.Length > 1 ? parts[..^1] : [];
        }

        var name = new JsonObject { ["family"] = family, ["given"] = new JsonArray(given.Select(g => (JsonNode)g).ToArray()) };
        if (config.GetBool("setText", true))
        {
            name["text"] = raw;
        }

        return TransformResult.Ok(name);
    }
}

/// <summary>13. Address Parsing &amp; Normalization — a simplified comma-delimited parser
/// ("line, city, state postalCode"); full address parsing libraries are out of scope here.</summary>
public sealed class AddressParsingNode : ITransformNode
{
    private static readonly Regex StateZip = new(@"^([A-Za-z]{2,})\s+(\d{5}(-\d{4})?)$", RegexOptions.Compiled);

    public TransformNodeType NodeType => TransformNodeType.AddressParsing;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var raw = value?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TransformResult.Ok(null);
        }

        var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var address = new JsonObject { ["line"] = new JsonArray(parts.Length > 0 ? (JsonNode)parts[0] : "") };

        if (parts.Length > 1)
        {
            address["city"] = parts[1];
        }

        if (parts.Length > 2)
        {
            var match = StateZip.Match(parts[2]);
            if (match.Success)
            {
                var state = match.Groups[1].Value;
                address["state"] = state.Length == 2 ? state.ToUpperInvariant() : state;
                address["postalCode"] = match.Groups[2].Value;
            }
            else
            {
                address["state"] = parts[2];
            }
        }

        address["use"] = config.Get("use", "home");
        return TransformResult.Ok(address);
    }
}

/// <summary>14. Telecom (ContactPoint) Normalization — E.164 for US-style 10-digit numbers; other regions
/// pass through with formatting stripped (full libphonenumber-equivalent parsing is out of scope here).</summary>
public sealed class TelecomNormalizationNode : ITransformNode
{
    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
    private static readonly Regex NonDigit = new(@"[^\d]", RegexOptions.Compiled);

    public TransformNodeType NodeType => TransformNodeType.TelecomNormalization;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var raw = value?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TransformResult.Ok(null);
        }

        if (EmailPattern.IsMatch(raw))
        {
            return TransformResult.Ok(new JsonObject
            {
                ["system"] = "email",
                ["value"] = raw,
                ["use"] = config.Get("use", "home")
            });
        }

        var digits = NonDigit.Replace(raw, string.Empty);
        var normalized = digits.Length switch
        {
            10 => $"+1{digits}",
            11 when digits.StartsWith('1') => $"+{digits}",
            _ => raw.StartsWith('+') ? raw : $"+{digits}"
        };

        return TransformResult.Ok(new JsonObject
        {
            ["system"] = "phone",
            ["value"] = normalized,
            ["use"] = config.Get("use", "mobile")
        });
    }
}
