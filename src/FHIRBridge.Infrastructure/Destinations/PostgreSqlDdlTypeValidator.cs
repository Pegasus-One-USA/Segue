using System.Globalization;
using System.Text.RegularExpressions;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Allowlists+normalizes a destination column data type against a curated set of PostgreSQL types — the
/// injection boundary for the type portion of DDL, since a type name can't be parameterized either. Mirrors
/// <see cref="SqlServerDdlTypeValidator"/>/<see cref="MySqlDdlTypeValidator"/> exactly (same public shape,
/// same allowlist-then-normalize approach), used by <see cref="SqlDestinationSchemaService"/>.
///
/// Also accepts the SQL-Server-flavored type strings the mapping canvas's own frontend defaults still generate
/// regardless of destination engine (field-mapping-add-column-modal.component.ts's FM_ADD_COLUMN_DATA_TYPES:
/// nvarchar/varchar/int/bigint/bit/date/datetime2/decimal/uniqueidentifier) — normalizing each to its real
/// PostgreSQL equivalent (uniqueidentifier -> uuid, bit -> boolean, datetime2 -> timestamp, nvarchar -> varchar,
/// '(max)' -> text, since PostgreSQL's VARCHAR has no unbounded/"MAX" form) — rather than requiring a separate
/// frontend change to be PostgreSQL-dialect-aware just to unblock this backend capability.
/// </summary>
internal static partial class PostgreSqlDdlTypeValidator
{
    /// <summary>Returns the normalized type text plus, for sized string types, the resolved max length (null for
    /// a fixed type, an unbounded TEXT normalization, or a decimal type).</summary>
    public static (string NormalizedType, int? MaxLength) Validate(string dataType)
    {
        var trimmed = dataType.Trim();

        if (FixedTypeMap.TryGetValue(trimmed, out var mapped))
        {
            return (mapped, null);
        }

        var sizedMatch = SizedStringTypeRegex().Match(trimmed);
        if (sizedMatch.Success)
        {
            var size = sizedMatch.Groups[2].Value.ToLowerInvariant();

            // PostgreSQL's VARCHAR has no "MAX" form (unlike SQL Server's nvarchar(max)) — an unbounded
            // string column becomes TEXT instead, same as MySqlDdlTypeValidator does for the identical
            // frontend default.
            if (size == "max")
            {
                return ("text", null);
            }

            var length = int.Parse(size, CultureInfo.InvariantCulture);
            if (length is < 1 or > 4000)
            {
                throw new InvalidOperationException($"'{dataType}' length must be between 1 and 4000, or MAX.");
            }

            // nvarchar/nchar (SQL-Server-flavored Unicode string types the frontend still sends regardless
            // of engine) have no distinct meaning in PostgreSQL — every text column is already UTF-8 — so
            // both normalize to plain varchar/char, same as MySqlDdlTypeValidator's own normalization.
            var family = sizedMatch.Groups[1].Value.ToLowerInvariant() switch
            {
                "nvarchar" => "varchar",
                "nchar" => "char",
                var f => f,
            };
            return ($"{family}({length})", length);
        }

        var decimalMatch = DecimalTypeRegex().Match(trimmed);
        if (decimalMatch.Success)
        {
            // PostgreSQL's DECIMAL and NUMERIC are true synonyms — normalized to "numeric" either way so
            // generated DDL is consistent regardless of which one was requested, same as MySqlDdlTypeValidator
            // does for its own decimal/numeric synonym pair.
            var precision = int.Parse(decimalMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            var scale = int.Parse(decimalMatch.Groups[3].Value, CultureInfo.InvariantCulture);
            // PostgreSQL's NUMERIC actually allows up to 1000 digits of precision, far beyond anything this
            // app's own frontend defaults ever request — capped at the same 38 SQL Server enforces so a
            // "decimal(p,s)" column means the same allowed range regardless of destination engine.
            if (precision is < 1 or > 38 || scale < 0 || scale > precision)
            {
                throw new InvalidOperationException($"'{dataType}' precision/scale is out of range.");
            }

            return ($"numeric({precision},{scale})", null);
        }

        throw new InvalidOperationException($"'{dataType}' is not an allowed data type.");
    }

    // Fixed (no size/precision) types — normalizes each SQL-Server-flavored keyword the frontend still sends
    // regardless of engine to its real PostgreSQL equivalent, while also accepting PostgreSQL's own native
    // spellings unchanged (e.g. a future PostgreSQL-aware caller, or a test, that already sends the right type).
    private static readonly Dictionary<string, string> FixedTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["int"] = "integer",
        ["integer"] = "integer",
        ["smallint"] = "smallint",
        ["bigint"] = "bigint",
        // "bit" is SQL Server's single-bit boolean keyword — PostgreSQL's own BIT type is a fixed-length bit
        // string (an entirely different thing), so this maps to PostgreSQL's real boolean type instead.
        ["bit"] = "boolean",
        ["boolean"] = "boolean",
        ["bool"] = "boolean",
        ["date"] = "date",
        ["datetime2"] = "timestamp",
        ["datetime"] = "timestamp",
        ["timestamp"] = "timestamp",
        // The Edit-column modal feeds a column's own live-probed type straight back as a candidate value
        // (see field-mapping-edit-column-modal.component.ts) — for a plain Postgres timestamp column, that
        // probed spelling is literally "timestamp without time zone" (information_schema's ANSI-standard
        // name for it), never bare "timestamp". Without this, re-saving (or editing) an existing timestamp
        // column untouched throws "'timestamp without time zone' is not an allowed data type."
        ["timestamp without time zone"] = "timestamp",
        // "datetimeoffset" is SQL Server's timezone-aware type — PostgreSQL's equivalent is "timestamptz"
        // (timestamp with time zone), which stores an unambiguous instant rather than a naive local value.
        // Also accepts Postgres's own two spellings for it unchanged.
        ["datetimeoffset"] = "timestamptz",
        ["timestamptz"] = "timestamptz",
        ["timestamp with time zone"] = "timestamptz",
        ["time"] = "time",
        // Same live-probe reasoning as "timestamp without time zone" above — Postgres's own ANSI-standard
        // name for an existing plain `time` column.
        ["time without time zone"] = "time",
        // "uniqueidentifier" is SQL Server's GUID keyword — PostgreSQL has a native, equivalent UUID type.
        ["uniqueidentifier"] = "uuid",
        ["uuid"] = "uuid",
        ["float"] = "double precision",
        ["real"] = "real",
        ["double precision"] = "double precision",
        ["text"] = "text",
        // Same live-probe reasoning again: Postgres's own information_schema.columns.data_type for an
        // existing sized VARCHAR column is the bare word "character varying" — the actual length lives in a
        // SEPARATE column (character_maximum_length) this validator is never handed, so there is no real size
        // to recover here. "text" (unbounded) is the safe choice: it can hold anything the original bounded
        // column could, so an untouched re-save only ever WIDENS the column, never truncates existing data.
        ["character varying"] = "text",
        // Same reasoning as "character varying": Postgres's own bare NUMERIC (no declared precision/scale) is
        // itself a fully valid, unbounded-precision/scale type — not an approximation like the "text" case
        // above, since this is exactly what the live probe's own name already means.
        ["numeric"] = "numeric",
    };

    [GeneratedRegex(@"^(nvarchar|varchar|nchar|char)\((max|\d{1,4})\)$", RegexOptions.IgnoreCase)]
    private static partial Regex SizedStringTypeRegex();

    [GeneratedRegex(@"^(decimal|numeric)\((\d{1,2}),\s*(\d{1,2})\)$", RegexOptions.IgnoreCase)]
    private static partial Regex DecimalTypeRegex();
}
