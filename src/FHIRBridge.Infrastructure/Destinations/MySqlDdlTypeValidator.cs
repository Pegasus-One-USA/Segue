using System.Globalization;
using System.Text.RegularExpressions;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Allowlists+normalizes a destination column data type against a curated set of MySQL types — the injection
/// boundary for the type portion of DDL, since a type name can't be parameterized either. Mirrors
/// <see cref="SqlServerDdlTypeValidator"/> exactly (same public shape, same allowlist-then-normalize approach),
/// used by <see cref="MySqlMappingSchemaTransaction"/>.
///
/// Also accepts the SQL-Server-flavored type strings the mapping canvas's own frontend defaults still generate
/// regardless of destination engine (field-mapping-summary.model.ts's DEFAULT_SQL_TYPE = 'nvarchar(max)',
/// DEFAULT_ID_TYPE = 'bigint') — normalizing 'nvarchar'/'nchar' to MySQL's 'varchar'/'char', and '(max)' to
/// MySQL's 'text' (MySQL's VARCHAR has no unbounded/"max" form) — rather than requiring a separate frontend
/// change to be MySQL-dialect-aware just to unblock this backend capability.
/// </summary>
internal static partial class MySqlDdlTypeValidator
{
    /// <summary>Returns the normalized type text plus, for sized string types, the resolved max length (null for
    /// a fixed type, an unbounded TEXT normalization, or a decimal type).</summary>
    public static (string NormalizedType, int? MaxLength) Validate(string dataType)
    {
        var trimmed = dataType.Trim();

        if (FixedTypeMap.TryGetValue(trimmed, out var mappedFixed))
        {
            return mappedFixed;
        }

        if (FixedDataTypes.Contains(trimmed))
        {
            return (trimmed.ToLowerInvariant(), null);
        }

        var sizedMatch = SizedStringTypeRegex().Match(trimmed);
        if (sizedMatch.Success)
        {
            var rawFamily = sizedMatch.Groups[1].Value.ToLowerInvariant();
            var family = NormalizeStringFamily(rawFamily);
            var size = sizedMatch.Groups[2].Value.ToLowerInvariant();

            // MySQL's VARCHAR/CHAR have no "MAX" form (and are capped well under 4000 chars once multi-byte
            // utf8mb4 encoding is accounted for) — an unbounded string column becomes TEXT instead, same as
            // MappedMySqlDestinationWriter.ColumnType already does for every mapped column value.
            if (size == "max")
            {
                return ("text", null);
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
            // MySQL's DECIMAL and NUMERIC are true synonyms (NUMERIC is implemented as DECIMAL) — normalized to
            // "decimal" either way so generated DDL is consistent regardless of which one was requested.
            var precision = int.Parse(decimalMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            var scale = int.Parse(decimalMatch.Groups[3].Value, CultureInfo.InvariantCulture);
            if (precision is < 1 or > 65 || scale < 0 || scale > precision)
            {
                throw new InvalidOperationException($"'{dataType}' precision/scale is out of range.");
            }

            return ($"decimal({precision},{scale})", null);
        }

        throw new InvalidOperationException($"'{dataType}' is not an allowed data type.");
    }

    private static string NormalizeStringFamily(string family) => family switch
    {
        "nvarchar" => "varchar",
        "nchar" => "char",
        _ => family,
    };

    /// <summary>Fixed keywords that mean something DIFFERENT in MySQL than what the frontend sends (or that
    /// MySQL has no native equivalent for at all) — checked before the plain pass-through allowlist below, so
    /// these translate instead of either being rejected or silently kept as a wrong/non-existent MySQL type.
    /// "datetime2" is SQL Server's own keyword (MySQL only has "datetime"/"timestamp"); "uniqueidentifier" is
    /// SQL Server's GUID type — MySQL has no native UUID storage type, so it becomes a fixed CHAR(36), wide
    /// enough for a UUID's canonical 36-character hyphenated string form (matching how a mapped UUID value
    /// is actually written — see MappedMySqlDestinationWriter's own string-shaped UUID handling).</summary>
    private static readonly Dictionary<string, (string NormalizedType, int? MaxLength)> FixedTypeMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["datetime2"] = ("datetime", null),
            ["uniqueidentifier"] = ("char(36)", 36),
        };

    private static readonly HashSet<string> FixedDataTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tinyint", "smallint", "mediumint", "int", "integer", "bigint",
        "bit", "boolean", "bool",
        "date", "datetime", "timestamp", "time",
        "float", "double", "real",
        "text", "tinytext", "mediumtext", "longtext",
        "blob", "tinyblob", "mediumblob", "longblob",
    };

    [GeneratedRegex(@"^(nvarchar|varchar|nchar|char)\((max|\d{1,4})\)$", RegexOptions.IgnoreCase)]
    private static partial Regex SizedStringTypeRegex();

    [GeneratedRegex(@"^(decimal|numeric)\((\d{1,2}),\s*(\d{1,2})\)$", RegexOptions.IgnoreCase)]
    private static partial Regex DecimalTypeRegex();
}
