using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
        if (defaultValue is not null)
        {
            return TransformResult.Ok(defaultValue);
        }

        // Nothing present and no default configured: optionally emit a structured data-absent-reason marker
        // (as a JsonObject, same as the FHIR complex-type builder nodes) instead of a bare null — a FHIR-native
        // destination writer can translate this into a real `_field.extension` data-absent-reason; a
        // SQL-shaped destination just sees a null column, since the marker only round-trips through JSON.
        if (config.GetBool("addDataAbsentReason", false))
        {
            return TransformResult.Ok(new System.Text.Json.Nodes.JsonObject { ["_dataAbsentReason"] = config.Get("dataAbsentReasonCode", "unknown") });
        }

        return TransformResult.Ok(null);
    }
}

/// <summary>19. Date Math / Age / Date-Shift.</summary>
public sealed class DateMathAgeNode : ITransformNode
{
    public TransformNodeType NodeType => TransformNodeType.DateMathAge;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var raw = value?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TransformResult.Ok(null);
        }

        // Same partial-precision handling as DateTimeFormatNode: a Safe-Harbor-generalized birthDate arrives
        // as a bare year ("1987") or year-month ("1987-05") — DateTime.TryParse rejects both outright, which
        // otherwise nulls out every age computation for every de-identified record. Treat a bare year/year-month
        // as January 1st of that year for age math; per the spec's "respect the original precision" rule, this
        // under-counts age by at most 11 months, never over-counts it.
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            var partialMatch = Regex.Match(raw, @"^(\d{4})(-\d{2})?$");
            if (!partialMatch.Success || !DateTime.TryParse($"{partialMatch.Groups[1].Value}-01-01", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                return TransformResult.Ok(null);
            }
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
                var shifted = ApplyIsoDuration(date, config.Get("duration", "P0D"));
                return TransformResult.Ok(shifted.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            case "shift":
                // Per-patient seeded offset (de-id date-shift): HMAC(secret, patientId) mod a configurable
                // window gives every date belonging to one patient the SAME offset, preserving inter-event
                // intervals, while different patients get different (but still stable) offsets. `_patientId` is
                // a reserved, caller-populated (never persisted) config key — see MappingNodeExecutor's
                // ApplyTransformRulesAsync. Falls back to the fixed `days` config when either input is missing
                // (no vault secret wired, or the caller didn't supply a patient id), matching pre-existing
                // behavior rather than failing pipelines that don't need per-patient consistency.
                var patientId = config.GetOrNull(ReservedTransformConfigKeys.PatientId);
                var shiftDays = !string.IsNullOrEmpty(secret) && patientId is not null
                    ? SeededShiftDays(secret, patientId, config.GetInt("maxShiftDays", 60))
                    : config.GetInt("days", 0);
                return TransformResult.Ok(date.AddDays(shiftDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            default:
                return TransformResult.Fail($"Unknown DateMathAge operation '{config.Get("operation")}'.");
        }
    }

    /// <summary>Adds a full ISO-8601 date duration (<c>P[n]Y[n]M[n]D</c> — the date-only subset; a time-of-day
    /// component after "T" is not meaningful for a date-precision shift and is ignored).</summary>
    private static DateTime ApplyIsoDuration(DateTime date, string duration)
    {
        var match = System.Text.RegularExpressions.Regex.Match(duration, @"^P(?:(-?\d+)Y)?(?:(-?\d+)M)?(?:(-?\d+)D)?$");
        if (!match.Success)
        {
            return date;
        }

        var years = match.Groups[1].Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var months = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        var days = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        return date.AddYears(years).AddMonths(months).AddDays(days);
    }

    /// <summary>Deterministic, patient-stable offset in [-maxShiftDays, +maxShiftDays], derived from an
    /// HMAC-SHA256 of the patient id keyed by the vault secret — same algorithm family as
    /// <see cref="HashingMaskingNode"/>'s hash mode, so both de-id primitives share one key.</summary>
    private static int SeededShiftDays(string secret, string patientId, int maxShiftDays)
    {
        var hash = System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(secret), System.Text.Encoding.UTF8.GetBytes(patientId));
        var magnitude = BitConverter.ToUInt32(hash, 0) % (uint)((2 * maxShiftDays) + 1);
        return (int)magnitude - maxShiftDays;
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
