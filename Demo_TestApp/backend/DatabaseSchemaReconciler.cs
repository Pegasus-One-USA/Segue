using System.Data;
using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
/// from the model; for every column missing from an existing table, ALTER TABLE ADD it; for every
/// HasData(...) seed row missing from an existing table, INSERT it. Strictly additive -- it never
/// drops or retypes anything that already exists, and never touches a row that's already present --
/// so it's safe to run unconditionally on every environment on every startup.
/// </summary>
public static class DatabaseSchemaReconciler
{
    public static void Reconcile(HealthAppDbContext db, ILogger logger)
    {
        var connection = db.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen)
        {
            connection.Open();
        }

        try
        {
            var (existingTables, existingColumnsByTable) = ReadCurrentSchema(connection);

            // The runtime model (db.Model) is EF Core's "read-optimized" model and deliberately strips
            // seed-data (HasData) metadata for performance -- GetSeedData() throws against it
            // ("please use DbContext.GetService<IDesignTimeModel>().Model") instead of just returning
            // empty, so the design-time model is required here, not db.Model. It retains everything the
            // runtime model has (table/column mappings, nullability, etc.) plus seed data, so it's a
            // safe superset to walk for every purpose below, not just seeding.
            var model = db.GetService<IDesignTimeModel>().Model;

            foreach (var entityType in model.GetEntityTypes())
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
                    ReconcileSeedData(connection, entityType, tableName, logger);
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
                        // Column already exists -- if the model says non-nullable but a NULL somehow
                        // got in (e.g. this exact column was added by an earlier, buggy version of this
                        // reconciler that didn't backfill new rows), heal it the same way a fresh ADD
                        // COLUMN would have. "WHERE ... IS NULL" no-ops instantly when there's nothing
                        // to fix, so this is cheap to run unconditionally on every startup, and it's
                        // what makes this self-healing rather than a one-time fix for today's bug.
                        //
                        // Primary-key / store-generated columns (identity Ids) are excluded outright:
                        // SQL Server rejects UPDATE against an IDENTITY column even when zero rows
                        // would match ("Cannot update identity column 'Id'"), and such a column can
                        // never actually be NULL anyway, so there's nothing to heal there.
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
                    // string on WorkflowSettingsEntity) MUST be backfilled on every existing row, not
                    // just added as NULL -- EF Core throws InvalidOperationException ("cannot be set to
                    // a null value because its type is 'string', which is not a nullable type") the
                    // moment it reads back a row where that column is NULL. SQL Server allows "ADD
                    // COLUMN NOT NULL" against a populated table as long as a DEFAULT accompanies it --
                    // it backfills every existing row with that default automatically, so this avoids
                    // both the ALTER failing outright AND the later read-time exception. A property the
                    // model marks nullable is simply added as NULL; reading NULL into a nullable CLR
                    // property is fine.
                    var columnClause = property.IsNullable
                        ? $"{columnType} NULL"
                        : $"{columnType} NOT NULL CONSTRAINT [DF_{tableName}_{columnName}] " +
                          $"DEFAULT ({property.GetDefaultValueSql() ?? SqlDefaultLiteralFor(property)})";

                    // EF1002 suppressed: tableName/columnName/columnType/columnClause come from our own
                    // compiled EF model metadata, never from external/user input -- SQL Server also has
                    // no parameter syntax for identifiers (table/column names), so this can't be
                    // rewritten as a parameterized statement regardless.
#pragma warning disable EF1002
                    db.Database.ExecuteSqlRaw($"ALTER TABLE [{tableName}] ADD [{columnName}] {columnClause}");
#pragma warning restore EF1002
                }

                ReconcileSeedData(connection, entityType, tableName, logger);
            }
        }
        finally
        {
            if (!wasOpen)
            {
                connection.Close();
            }
        }
    }

    private static (HashSet<string> Tables, Dictionary<string, HashSet<string>> ColumnsByTable) ReadCurrentSchema(
        DbConnection connection)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columnsByTable = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

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

    // Inserts any HasData(...) seed row that's missing from an already-existing table -- the same class
    // of gap as a missing column: EnsureCreated() only applies HasData seeding when it builds a table
    // for the very first time, so a table that already existed before a HasData() row was added to the
    // model (or before the whole table itself predates HasData being used at all) never gets that row.
    // That's exactly what caused WorkflowSettings' Id=1 row to be missing on an environment whose table
    // predates it, which then surfaced as "Cannot insert explicit value for identity column ... when
    // IDENTITY_INSERT is set to OFF" the first time the app tried to create it itself (EF only omits an
    // identity column from its own INSERT when the value is left at the CLR default; an explicitly
    // assigned Id=1 makes EF try to insert it literally). Driven entirely by entityType.GetSeedData() --
    // no hardcoded table/row values -- so any future HasData(...) call is covered the same way.
    private static void ReconcileSeedData(DbConnection connection, IEntityType entityType, string tableName, ILogger logger)
    {
        var seedRows = entityType.GetSeedData().ToList();
        if (seedRows.Count == 0)
        {
            return;
        }

        var primaryKey = entityType.FindPrimaryKey();
        if (primaryKey is null)
        {
            return;
        }

        var propertiesByName = entityType.GetProperties().ToDictionary(p => p.Name);
        var isSingleIdentityKey = primaryKey.Properties.Count == 1
            && primaryKey.Properties[0].ValueGenerated == ValueGenerated.OnAdd
            && (primaryKey.Properties[0].ClrType == typeof(int) || primaryKey.Properties[0].ClrType == typeof(long));

        foreach (var row in seedRows)
        {
            var pkColumns = new List<(string ColumnName, object? Value)>();
            var missingPkValue = false;
            foreach (var pkProperty in primaryKey.Properties)
            {
                if (!row.TryGetValue(pkProperty.Name, out var pkValue))
                {
                    missingPkValue = true;
                    break;
                }

                pkColumns.Add((pkProperty.GetColumnName(), pkValue));
            }

            if (missingPkValue)
            {
                // Can't identify this seed row without its full key -- skip rather than guess.
                continue;
            }

            using (var existsCmd = connection.CreateCommand())
            {
                var whereParts = new List<string>();
                for (var i = 0; i < pkColumns.Count; i++)
                {
                    var paramName = $"@pk{i}";
                    whereParts.Add($"[{pkColumns[i].ColumnName}] = {paramName}");
                    var p = existsCmd.CreateParameter();
                    p.ParameterName = paramName;
                    p.Value = pkColumns[i].Value ?? DBNull.Value;
                    existsCmd.Parameters.Add(p);
                }

                existsCmd.CommandText = $"SELECT COUNT(1) FROM [{tableName}] WHERE {string.Join(" AND ", whereParts)}";
                var existingCount = Convert.ToInt32(existsCmd.ExecuteScalar());
                if (existingCount > 0)
                {
                    continue;
                }
            }

            logger.LogInformation("SchemaReconciler: inserting missing seed row into [{Table}].", tableName);

            using var insertCmd = connection.CreateCommand();
            var columnNames = new List<string>();
            var paramNames = new List<string>();
            var idx = 0;
            foreach (var kvp in row)
            {
                if (!propertiesByName.TryGetValue(kvp.Key, out var property))
                {
                    // Shadow/unmapped property in the seed dictionary -- skip defensively rather than
                    // guess at a column name.
                    continue;
                }

                var paramName = $"@p{idx++}";
                columnNames.Add($"[{property.GetColumnName()}]");
                paramNames.Add(paramName);
                var p = insertCmd.CreateParameter();
                p.ParameterName = paramName;
                p.Value = kvp.Value ?? DBNull.Value;
                insertCmd.Parameters.Add(p);
            }

            var insertSql = $"INSERT INTO [{tableName}] ({string.Join(", ", columnNames)}) VALUES ({string.Join(", ", paramNames)})";

            // The seed data explicitly assigns the key (e.g. Id = 1) even when the column is a real
            // IDENTITY column at the SQL Server level -- SQL Server rejects an explicit identity value
            // without this toggle ("Cannot insert explicit value for identity column ... IDENTITY_INSERT
            // is set to OFF").
            insertCmd.CommandText = isSingleIdentityKey
                ? $"SET IDENTITY_INSERT [{tableName}] ON; {insertSql}; SET IDENTITY_INSERT [{tableName}] OFF;"
                : insertSql;
            insertCmd.ExecuteNonQuery();
        }
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
