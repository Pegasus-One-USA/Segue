using System.Globalization;
using System.Text.RegularExpressions;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Allowlists+normalizes a destination column data type against the types a Microsoft Fabric Warehouse actually
/// supports. Mirrors <see cref="SqlServerDdlTypeValidator"/>'s public shape exactly (same
/// <c>(NormalizedType, MaxLength)</c> return, same exception-on-rejection contract), because a Fabric Warehouse
/// speaks T-SQL over TDS and everything else about the DDL path is shared with SQL Server.
///
/// <para><b>Why this is not just SqlServerDdlTypeValidator.</b> Fabric Warehouse supports a deliberately narrow
/// subset of T-SQL data types, and reusing SQL Server's allowlist would let the mapping canvas offer columns a
/// real Warehouse then rejects — the failure landing at the worst moment, against a live tenant, rather than at
/// validation time. The differences that matter here:</para>
/// <list type="bullet">
/// <item><description><c>nvarchar</c>/<c>nchar</c> are not supported — Fabric Warehouse stores strings as
/// <c>varchar</c> with a UTF-8 collation, so Unicode is handled without the national-character types. A requested
/// <c>nvarchar</c> is normalized to <c>varchar</c> rather than rejected, since it is what the caller meant and the
/// canvas's SQL-Server-shaped defaults produce it constantly.</description></item>
/// <item><description><c>MAX</c>-length strings are not supported; Fabric caps <c>varchar</c> at 8000. A "max"
/// request resolves to <c>varchar(8000)</c> instead of failing, for the same reason.</description></item>
/// <item><description><c>tinyint</c>, <c>smallint</c>, <c>datetime2</c> precision variants, <c>uniqueidentifier</c>,
/// <c>money</c>, <c>xml</c> and the LOB types are not supported at all, so they are rejected outright — there is no
/// honest silent substitution for them.</description></item>
/// </list>
///
/// <para><b>Unverified against a live Fabric tenant</b>, like the rest of the Warehouse path (see
/// <c>WarehouseTableLandingStrategy</c> and <c>FabricWarehouseConnectionFactory</c>). The subset below follows
/// Microsoft's published Fabric Warehouse data-type list; if a table definition is rejected for a type allowed
/// here, this allowlist is the first place to look.</para>
/// </summary>
internal static partial class FabricWarehouseDdlTypeValidator
{
    /// <summary>Fabric Warehouse's maximum varchar/char length. SQL Server allows 4000 for nvarchar and MAX for
    /// LOB-backed strings; Fabric allows neither, capping at 8000 single-byte characters.</summary>
    private const int MaxStringLength = 8000;

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
            // nvarchar -> varchar, nchar -> char. Fabric's varchar is UTF-8 collated, so this preserves the
            // caller's intent (a Unicode-capable string column) rather than silently narrowing it.
            var requestedFamily = sizedMatch.Groups[1].Value.ToLowerInvariant();
            var family = requestedFamily switch
            {
                "nvarchar" => "varchar",
                "nchar" => "char",
                _ => requestedFamily,
            };

            var size = sizedMatch.Groups[2].Value.ToLowerInvariant();
            if (size == "max")
            {
                return ($"{family}({MaxStringLength})", MaxStringLength);
            }

            var length = int.Parse(size, CultureInfo.InvariantCulture);
            if (length is < 1 || length > MaxStringLength)
            {
                throw new InvalidOperationException(
                    $"'{dataType}' length must be between 1 and {MaxStringLength} for a Fabric Warehouse "
                        + "(it does not support MAX-length or nvarchar columns).");
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

        throw new InvalidOperationException(
            $"'{dataType}' is not a data type a Fabric Warehouse supports.");
    }

    /// <summary>
    /// Fabric Warehouse's supported non-parameterized types. Deliberately SHORTER than SQL Server's list:
    /// <c>tinyint</c>, <c>uniqueidentifier</c> and <c>real</c> are absent because Fabric does not support them,
    /// and <c>datetime2</c> is included only in its bare form (Fabric fixes the precision at 6).
    /// </summary>
    private static readonly HashSet<string> FixedDataTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "bigint", "smallint", "bit", "date", "datetime2", "time", "float", "varbinary"
    };

    [GeneratedRegex(@"^(nvarchar|varchar|char|nchar)\((max|\d{1,4})\)$", RegexOptions.IgnoreCase)]
    private static partial Regex SizedStringTypeRegex();

    [GeneratedRegex(@"^(decimal|numeric)\((\d{1,2}),\s*(\d{1,2})\)$", RegexOptions.IgnoreCase)]
    private static partial Regex DecimalTypeRegex();
}
