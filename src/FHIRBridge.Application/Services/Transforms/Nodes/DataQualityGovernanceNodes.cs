using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Transforms.Nodes;

/// <summary>18. Default / Null Handling &amp; Coalesce. Expects <paramref name="value"/> to be either a single
/// value or an ordered <see cref="IEnumerable{T}"/> of candidates (coalesce mode).</summary>
public sealed class DefaultNullHandlingNode : ITransformNode
{
    public TransformNodeType NodeType => TransformNodeType.DefaultNullHandling;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var sentinels = config.Get("sentinels", "N/A,UNKNOWN,9999")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        var firstPresent = value.AsItems().FirstOrDefault(c =>
        {
            var s = c?.ToString();
            return !string.IsNullOrWhiteSpace(s) && !sentinels.Contains(s, StringComparer.OrdinalIgnoreCase);
        });

        if (firstPresent is not null)
        {
            return TransformResult.Ok(firstPresent);
        }

        var defaultValue = config.GetOrNull("default");
        return TransformResult.Ok(defaultValue);
    }
}

/// <summary>19. Date Math / Age / Date-Shift.</summary>
public sealed class DateMathAgeNode : ITransformNode
{
    public TransformNodeType NodeType => TransformNodeType.DateMathAge;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var raw = value?.ToString();
        if (string.IsNullOrWhiteSpace(raw) || !DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return TransformResult.Ok(null);
        }

        switch (config.Get("operation", "age"))
        {
            case "age":
                var reference = DateTime.TryParse(config.GetOrNull("referenceDate"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var refDate)
                    ? refDate
                    : DateTime.UtcNow;
                var age = reference.Year - date.Year;
                if (date.Date > reference.Date.AddYears(-age))
                {
                    age--;
                }

                if (config.GetBool("redactOver89", true) && age > 89)
                {
                    return TransformResult.Ok(null);
                }

                return TransformResult.Ok(age);

            case "add":
                var days = ParseIsoDurationDays(config.Get("duration", "P0D"));
                return TransformResult.Ok(date.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            case "shift":
                // Fixed per-request offset from config; a true per-patient seeded offset needs a keyed generator
                // supplied by the caller (via `secret`) so the same patient always shifts by the same amount —
                // out of scope for this simplified node.
                var shiftDays = config.GetInt("days", 0);
                return TransformResult.Ok(date.AddDays(shiftDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            default:
                return TransformResult.Fail($"Unknown DateMathAge operation '{config.Get("operation")}'.");
        }
    }

    private static int ParseIsoDurationDays(string duration)
    {
        var match = System.Text.RegularExpressions.Regex.Match(duration, @"^P(-?\d+)D$");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }
}

/// <summary>20. Hashing / Masking / Redaction — the field-level de-identification primitive.
/// <paramref name="secret"/> is the HMAC key, resolved by the caller from the vault; it is never read from
/// <paramref name="config"/> so it can never end up persisted in a rule's ConfigJson.</summary>
public sealed class HashingMaskingNode : ITransformNode
{
    public TransformNodeType NodeType => TransformNodeType.HashingMasking;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var raw = value?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TransformResult.Ok(null);
        }

        return config.Get("mode", "mask") switch
        {
            "hash" => Hash(raw, secret),
            "redact" => TransformResult.Ok(config.GetOrNull("token")),
            _ => Mask(raw, config.GetInt("keepLength", 4))
        };
    }

    private static TransformResult Hash(string raw, string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return TransformResult.Fail("Hashing requires a vault-resolved secret key.");
        }

        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(keyBytes, Encoding.UTF8.GetBytes(raw));
        return TransformResult.Ok(Convert.ToHexString(hash).ToLowerInvariant());
    }

    private static TransformResult Mask(string raw, int keepLength)
    {
        if (raw.Length <= keepLength)
        {
            return TransformResult.Ok(raw);
        }

        var masked = new string('*', raw.Length - keepLength) + raw[^keepLength..];
        return TransformResult.Ok(masked);
    }
}
