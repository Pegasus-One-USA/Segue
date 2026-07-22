using System.Globalization;
using System.Text.RegularExpressions;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Allowlists+normalizes a destination column data type against a curated set of SQL Server types — the
/// injection boundary for the type portion of DDL, since a type name can't be parameterized either. Shared
/// by every service that executes real SQL Server DDL (<see cref="SqlDestinationSchemaService"/>,
/// <see cref="SqlServerMappingSchemaProvider"/>) so the allowlist is declared in exactly one place.
/// </summary>
internal static partial class SqlServerDdlTypeValidator
{
    /// <summary>Returns the normalized type text plus, for sized string types, the resolved max length.</summary>
    public static (string NormalizedType, int? MaxLength) Validate(string dataType)
    {
        var trimmed = dataType.Trim();

        if (FixedDataTypes.Contains(trimmed))
        {
            return (trimmed.ToLowerInvariant(), null);
        }

        var sizedMatch = SizedStringTypeRegex().Match(trimmed);
        if (sizedMatch.Success)
        {
            var family = sizedMatch.Groups[1].Value.ToLowerInvariant();
            var size = sizedMatch.Groups[2].Value.ToLowerInvariant();
            if (size == "max")
            {
                return ($"{family}(max)", null);
            }

            var length = int.Parse(size, CultureInfo.InvariantCulture);
            if (length is < 1 or > 4000)
            {
                throw new InvalidOperationException($"'{dataType}' length must be between 1 and 4000, or MAX.");
            }

            return ($"{family}({length})", length);
        }

        var decimalMatch = DecimalTypeRegex().Match(trimmed);
        if (decimalMatch.Success)
        {
            var family = decimalMatch.Groups[1].Value.ToLowerInvariant();
            var precision = int.Parse(decimalMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            var scale = int.Parse(decimalMatch.Groups[3].Value, CultureInfo.InvariantCulture);
            if (precision is < 1 or > 38 || scale < 0 || scale > precision)
            {
                throw new InvalidOperationException($"'{dataType}' precision/scale is out of range.");
            }

            return ($"{family}({precision},{scale})", null);
        }

        throw new InvalidOperationException($"'{dataType}' is not an allowed data type.");
    }

    private static readonly HashSet<string> FixedDataTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "bigint", "smallint", "tinyint", "bit", "date", "datetime2", "time", "uniqueidentifier", "float", "real"
    };

    [GeneratedRegex(@"^(nvarchar|varchar|char|nchar)\((max|\d{1,4})\)$", RegexOptions.IgnoreCase)]
    private static partial Regex SizedStringTypeRegex();

    [GeneratedRegex(@"^(decimal|numeric)\((\d{1,2}),\s*(\d{1,2})\)$", RegexOptions.IgnoreCase)]
    private static partial Regex DecimalTypeRegex();
}
