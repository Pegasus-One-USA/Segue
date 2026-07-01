namespace FHIRBridge.Integration.Sql;

/// <summary>Helpers for safely embedding SQL Server identifiers (schema, table, database names) in DDL.</summary>
public static class SqlIdentifier
{
    /// <summary>Escapes a bracket-quoted identifier by doubling any closing brackets, e.g. <c>a]b</c> → <c>a]]b</c>.</summary>
    public static string Escape(string value)
    {
        return value.Replace("]", "]]", StringComparison.Ordinal);
    }
}
