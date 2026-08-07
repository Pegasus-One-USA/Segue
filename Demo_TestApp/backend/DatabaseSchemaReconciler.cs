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
                    continue;
                }

                var columnType = property.GetColumnType();
                logger.LogInformation(
                    "SchemaReconciler: adding missing column [{Table}].[{Column}] ({Type}).",
                    tableName, columnName, columnType);

                // Always added NULLable regardless of the model's own nullability -- an existing table
                // can already have rows, and SQL Server rejects "ADD COLUMN NOT NULL" against a
                // populated table unless every existing row also gets a value. Nullable-always
                // sidesteps guessing at a plausible per-column default; application code already
                // treats most of these flattened fields as optional/nullable anyway.
                //
                // EF1002 suppressed: tableName/columnName/columnType come from our own compiled EF
                // model metadata, never from external/user input -- SQL Server also has no parameter
                // syntax for identifiers (table/column names), so this can't be rewritten as a
                // parameterized statement regardless.
#pragma warning disable EF1002
                db.Database.ExecuteSqlRaw($"ALTER TABLE [{tableName}] ADD [{columnName}] {columnType} NULL");
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
}
