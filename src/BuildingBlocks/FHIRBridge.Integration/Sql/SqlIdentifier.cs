using System.Text.RegularExpressions;

namespace FHIRBridge.Integration.Sql;

/// <summary>Helpers for safely embedding SQL Server identifiers (schema, table, database names) in DDL.</summary>
public static partial class SqlIdentifier
{
    /// <summary>Escapes a bracket-quoted identifier by doubling any closing brackets, e.g. <c>a]b</c> → <c>a]]b</c>.</summary>
    public static string Escape(string value)
    {
        return value.Replace("]", "]]", StringComparison.Ordinal);
    }

    /// <summary>
    /// Strictly allowlists a schema/table/column name before it's interpolated into DDL — the actual
    /// injection boundary for identifiers, which can't be parameterized the way values can. Rejects
    /// anything but a plain identifier (letters/digits/underscore, not starting with a digit); callers
    /// that need a schema-qualified name should split and validate each part separately.
    /// </summary>
    public static string Validate(string identifier)
    {
        if (!ValidIdentifierRegex().IsMatch(identifier))
        {
            throw new InvalidOperationException($"'{identifier}' is not a valid SQL identifier.");
        }

        return identifier;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex ValidIdentifierRegex();
}
