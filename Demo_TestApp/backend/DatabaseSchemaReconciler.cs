using System.Data;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace HealthAppBackend;

/// <summary>
/// Generic, model-driven schema reconciler -- runs once at every startup (see Program.cs) and closes
/// the one gap EnsureCreated() deliberately leaves open: EnsureCreated() only builds a schema when the
/// database doesn't exist AT ALL, and never revisits an already-existing database even after new
/// entities/properties are added to the model. Before this existed, every schema change here required
/// a hand-written "IF NOT EXISTS(...)" / "IF COL_LENGTH(...) IS NULL" patch in Program.cs -- easy to
/// miss entirely on some environment's database (see the Practitioner/WorkflowSettings columns and the
/// Practitioner/Encounter/AllergyIntolerance/etc. tables that were never patched anywhere), and it
/// doesn't scale as more entities get added.
///
/// This walks the live EF model instead: for every mapped table missing from the database, CREATE it
/// from the model; for every column missing from an existing table, ALTER TABLE ADD it. Strictly
/// additive -- it never drops or retypes anything that already exists -- so it's safe to run
/// unconditionally on every environment on every startup.
/// </summary>
public static class DatabaseSchemaReconciler
{
    public static void Reconcile(HealthAppDbContext db, ILogger logger)
    {
        var (existingTables, existingColumnsByTable) = ReadCurrentSchema(db);

        foreach (var entityType in db.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (tableName is null)
            {
                // Owned/no-table entity -- nothing to reconcile.
                continue;
            }

            var properties = entityType.GetProperties().ToList();

            if (!existingTables.Contains(tableName))
            {
                var sql = BuildCreateTableSql(tableName, entityType, properties);
                logger.LogInformation("SchemaReconciler: creating missing table [{Table}].", tableName);
                db.Database.ExecuteSqlRaw(sql);
                continue;
            }

            var existingColumns = existingColumnsByTable.TryGetValue(tableName, out var cols)
                ? cols
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in properties)
            {
                var columnName = property.GetColumnName();
                if (existingColumns.Contains(columnName))
                {
                    // Column already exists -- if the model says non-nullable but a NULL somehow got
                    // in (e.g. this exact column was added by an earlier, buggy version of this
                    // reconciler that didn't backfill new rows), heal it the same way a fresh ADD
                    // COLUMN would have. "WHERE ... IS NULL" no-ops instantly when there's nothing to
                    // fix, so this is cheap to run unconditionally on every startup, and it's what
                    // makes this self-healing rather than a one-time fix for today's specific bug.
                    //
                    // Primary-key / store-generated columns (identity Ids) are excluded outright: SQL
                    // Server rejects UPDATE against an IDENTITY column even when zero rows would match
                    // ("Cannot update identity column 'Id'"), and such a column can never actually be
                    // NULL anyway, so there's nothing to heal there.
                    var isStoreGenerated = property.IsPrimaryKey() || property.ValueGenerated == ValueGenerated.OnAdd;
                    if (!property.IsNullable && !isStoreGenerated)
                    {
                        var backfillLiteral = property.GetDefaultValueSql() ?? SqlDefaultLiteralFor(property);
#pragma warning disable EF1002
                        db.Database.ExecuteSqlRaw(
                            $"UPDATE [{tableName}] SET [{columnName}] = {backfillLiteral} WHERE [{columnName}] IS NULL");
#pragma warning restore EF1002
                    }

                    continue;
                }

                var columnType = property.GetColumnType();
                logger.LogInformation(
                    "SchemaReconciler: adding missing column [{Table}].[{Column}] ({Type}).",
                    tableName, columnName, columnType);

                // A property the model marks non-nullable (e.g. every "= string.Empty"-defaulted
                // string on WorkflowSettingsEntity) MUST be backfilled on every existing row, not just
                // added as NULL -- EF Core throws InvalidOperationException ("cannot be set to a null
                // value because its type is 'string', which is not a nullable type") the moment it
                // reads back a row where that column is NULL. SQL Server allows "ADD COLUMN NOT NULL"
                // against a populated table as long as a DEFAULT accompanies it -- it backfills every
                // existing row with that default automatically, so this avoids both the ALTER failing
                // outright AND the later read-time exception. A property the model marks nullable is
                // simply added as NULL; reading NULL into a nullable CLR property is fine.
                var columnClause = property.IsNullable
                    ? $"{columnType} NULL"
                    : $"{columnType} NOT NULL CONSTRAINT [DF_{tableName}_{columnName}] " +
                      $"DEFAULT ({property.GetDefaultValueSql() ?? SqlDefaultLiteralFor(property)})";

                // EF1002 suppressed: tableName/columnName/columnType/columnClause come from our own
                // compiled EF model metadata, never from external/user input -- SQL Server also has no
                // parameter syntax for identifiers (table/column names), so this can't be rewritten as
                // a parameterized statement regardless.
#pragma warning disable EF1002
                db.Database.ExecuteSqlRaw($"ALTER TABLE [{tableName}] ADD [{columnName}] {columnClause}");
#pragma warning restore EF1002
            }
        }
    }

    private static (HashSet<string> Tables, Dictionary<string, HashSet<string>> ColumnsByTable) ReadCurrentSchema(
        HealthAppDbContext db)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columnsByTable = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        var connection = db.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen)
        {
            connection.Open();
        }

        try
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sys.tables";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT t.name AS TableName, c.name AS ColumnName " +
                    "FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var tableName = reader.GetString(0);
                    var columnName = reader.GetString(1);
                    if (!columnsByTable.TryGetValue(tableName, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        columnsByTable[tableName] = set;
                    }

                    set.Add(columnName);
                }
            }
        }
        finally
        {
            if (!wasOpen)
            {
                connection.Close();
            }
        }

        return (tables, columnsByTable);
    }

    private static string BuildCreateTableSql(string tableName, IEntityType entityType, List<IProperty> properties)
    {
        var columnDefs = new List<string>();

        foreach (var property in properties)
        {
            var columnName = property.GetColumnName();
            var columnType = property.GetColumnType();
            var nullable = property.IsNullable ? "NULL" : "NOT NULL";

            var isIdentity = property.IsPrimaryKey()
                && property.ValueGenerated == ValueGenerated.OnAdd
                && (property.ClrType == typeof(int) || property.ClrType == typeof(long));
            var identityClause = isIdentity ? " IDENTITY(1,1)" : string.Empty;

            var defaultSql = property.GetDefaultValueSql();
            var defaultClause = defaultSql is null ? string.Empty : $" DEFAULT ({defaultSql})";

            columnDefs.Add($"    [{columnName}] {columnType}{identityClause} {nullable}{defaultClause}");
        }

        var primaryKey = entityType.FindPrimaryKey();
        if (primaryKey is not null)
        {
            var keyColumns = string.Join(", ", primaryKey.Properties.Select(p => $"[{p.GetColumnName()}]"));
            columnDefs.Add($"    CONSTRAINT [PK_{tableName}] PRIMARY KEY ({keyColumns})");
        }

        var sb = new StringBuilder();
        sb.Append("CREATE TABLE [").Append(tableName).Append("] (\n");
        sb.Append(string.Join(",\n", columnDefs));
        sb.Append("\n)");
        return sb.ToString();
    }

    // Backfill value for a NOT NULL column being added to a table that may already have rows, when the
    // model itself doesn't specify one via HasDefaultValueSql. Covers every CLR type actually used
    // across this app's entities today -- throws rather than guessing for anything else, since a wrong
    // silent guess (e.g. defaulting a domain-specific numeric column to 0) could be worse than a loud
    // failure that tells you to add a mapping or make the property nullable.
    private static string SqlDefaultLiteralFor(IProperty property)
    {
        if (property.ClrType == typeof(string))
        {
            return "''";
        }

        if (property.ClrType == typeof(bool) || property.ClrType == typeof(int) || property.ClrType == typeof(long)
            || property.ClrType == typeof(short) || property.ClrType == typeof(decimal)
            || property.ClrType == typeof(double) || property.ClrType == typeof(float))
        {
            return "0";
        }

        if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTimeOffset))
        {
            return "SYSUTCDATETIME()";
        }

        if (property.ClrType == typeof(Guid))
        {
            return "'00000000-0000-0000-0000-000000000000'";
        }

        throw new NotSupportedException(
            $"DatabaseSchemaReconciler has no default-literal mapping for CLR type '{property.ClrType}' " +
            $"(property '{property.Name}') on a NOT NULL column being added to an existing table. Either add " +
            "a mapping here, make the property nullable in the model, or give it an explicit " +
            "HasDefaultValueSql(...) in OnModelCreating.");
    }
}
