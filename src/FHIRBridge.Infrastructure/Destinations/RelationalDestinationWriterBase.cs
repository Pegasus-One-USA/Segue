using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Provider-agnostic relational destination writer built on ADO.NET (<see cref="DbConnection"/>). The customer owns
/// the destination schema: the target table must already exist and every written column must be one the mapping
/// profile explicitly maps. Writes mapped records in Insert, Upsert, or Update mode. Upsert and Update both require
/// one mapped field flagged <see cref="MappingField.IsUpsertKey"/>. Upsert is implemented as a portable
/// delete-by-key + insert within a transaction (last-write-wins) so it works on any dialect without requiring a
/// pre-existing unique index; Update issues a plain <c>UPDATE ... WHERE key = @key</c> and never inserts. Concrete
/// subclasses supply the dialect (connection, identifier quoting, type mapping, table-existence check). Mirrors
/// <see cref="MappedSqlServerDestinationWriter"/> for SQL Server / Azure SQL.
/// </summary>
public abstract partial class RelationalDestinationWriterBase : IConfiguredDestinationWriter
{
    private readonly ISecretProvider _secretProvider;

    protected RelationalDestinationWriterBase(ISecretProvider secretProvider)
    {
        _secretProvider = secretProvider;
    }

    // --- Dialect hooks ---
    protected abstract DbConnection CreateConnection(string connectionString);
    protected abstract string DefaultSchema { get; }
    protected abstract string Quote(string identifier);
    protected abstract string ColumnType(MappingValueType valueType);
    protected abstract string BuildTableExistsSql(string schema, string table);

    /// <summary>Qualified <c>schema.table</c> (or just the quoted table when the dialect has no schemas).</summary>
    protected virtual string QualifiedName(string schema, string table)
        => string.IsNullOrEmpty(schema) ? Quote(table) : $"{Quote(schema)}.{Quote(table)}";

    public async Task<DestinationWriteResult> WriteAsync(
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        PipelineWriteContext context,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return new DestinationWriteResult(0);
        }

        var connectionString = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        var target = ParseTarget(destination.Target ?? mappingProfile.DestinationObject, mappingProfile);

        await using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await EnsureTableAsync(connection, target, mappingProfile, cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var record in records)
        {
            if (target.UpdateOnly)
            {
                // Update-only never inserts: a record with no key value has nothing to match, so it's skipped
                // entirely rather than falling back to Insert.
                if (TryGetKeyValue(record, target.KeyColumn!, out var updateKeyValue))
                {
                    await UpdateRecordAsync(connection, transaction, target, record, updateKeyValue, cancellationToken);
                }

                continue;
            }

            if (target.Upsert && TryGetKeyValue(record, target.KeyColumn!, out var keyValue))
            {
                await DeleteByKeyAsync(connection, transaction, target, keyValue, cancellationToken);
            }

            await InsertRecordAsync(connection, transaction, target, record, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new DestinationWriteResult(records.Count);
    }

    private async Task EnsureTableAsync(
        DbConnection connection,
        RelationalTarget target,
        MappingProfile mappingProfile,
        CancellationToken cancellationToken)
    {
        var hasMappedFields = mappingProfile.Fields.Any(field =>
            (string.IsNullOrWhiteSpace(field.ResourceType) ||
                string.Equals(field.ResourceType, mappingProfile.ResourceType, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(field.DestinationObject) ||
                string.Equals(field.DestinationObject, mappingProfile.DestinationObject, StringComparison.OrdinalIgnoreCase)));

        if (!hasMappedFields)
        {
            throw new InvalidOperationException(
                $"Mapping profile for '{target.Schema}.{target.Table}' has no mapped fields — a SQL destination needs at least one mapped column.");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = BuildTableExistsSql(target.Schema, target.Table);
        var result = await command.ExecuteScalarAsync(cancellationToken);

        if (result is null or DBNull)
        {
            throw new InvalidOperationException(
                $"Destination table '{QualifiedName(target.Schema, target.Table)}' does not exist. Create it in your database before running this pipeline.");
        }
    }

    private async Task InsertRecordAsync(
        DbConnection connection,
        DbTransaction transaction,
        RelationalTarget target,
        MappedDestinationRecord record,
        CancellationToken cancellationToken)
    {
        var columns = record.Values.Keys
            .Select(ValidateIdentifier)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (columns.Count == 0)
        {
            return;
        }

        var sql = $"INSERT INTO {QualifiedName(target.Schema, target.Table)} " +
                  $"({string.Join(", ", columns.Select(Quote))}) VALUES " +
                  $"({string.Join(", ", columns.Select(c => "@" + c))})";

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var column in columns)
        {
            AddParameter(command, column, Stringify(record.Values[column]));
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeleteByKeyAsync(
        DbConnection connection,
        DbTransaction transaction,
        RelationalTarget target,
        object? keyValue,
        CancellationToken cancellationToken)
    {
        var sql = $"DELETE FROM {QualifiedName(target.Schema, target.Table)} " +
                  $"WHERE {Quote(target.KeyColumn!)} = @KeyValue";

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameter(command, "KeyValue", Stringify(keyValue));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Updates the matching row by <see cref="RelationalTarget.KeyColumn"/> only — never inserts. Called
    /// only when the record actually has a key value (see <see cref="WriteAsync"/>); a record with no matching row
    /// is simply left unwritten, which is exactly what "Update only" means.</summary>
    private async Task UpdateRecordAsync(
        DbConnection connection,
        DbTransaction transaction,
        RelationalTarget target,
        MappedDestinationRecord record,
        object? keyValue,
        CancellationToken cancellationToken)
    {
        var columns = record.Values.Keys
            .Select(ValidateIdentifier)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(column => !string.Equals(column, target.KeyColumn, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (columns.Count == 0)
        {
            return;
        }

        var setClause = string.Join(", ", columns.Select(column => $"{Quote(column)} = @{column}"));
        var sql = $"UPDATE {QualifiedName(target.Schema, target.Table)} SET {setClause} " +
                  $"WHERE {Quote(target.KeyColumn!)} = @KeyValue";

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var column in columns)
        {
            AddParameter(command, column, Stringify(record.Values[column]));
        }
        AddParameter(command, "KeyValue", Stringify(keyValue));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@" + name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    // The portable relational writer stores every value as text so inserts never fail on cross-dialect type
    // coercion — but .NET's default ToString() for DateTime/DateOnly ("MM/dd/yyyy HH:mm:ss") isn't a date literal
    // any SQL dialect accepts, so date/time and boolean values need their own explicit, dialect-portable format
    // (ISO 8601 date/time; "0"/"1" for boolean) rather than falling through to Convert.ToString.
    private static object Stringify(object? value) => value switch
    {
        null => DBNull.Value,
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool boolean => boolean ? "1" : "0",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static bool TryGetKeyValue(MappedDestinationRecord record, string keyColumn, out object? keyValue)
        => record.Values.TryGetValue(keyColumn, out keyValue) && keyValue is not null;

    private RelationalTarget ParseTarget(string destinationObject, MappingProfile mappingProfile)
    {
        // A stored destination object may carry a ';mode=<writeMode>' (or legacy '?key=') suffix — neither is part
        // of the table identifier, so both must be stripped before any '.'-split / identifier validation runs.
        // Splitting on ';' first (mirroring MappedSqlServerDestinationWriter.ParseDestinationTarget) matters most
        // for a schema-less dialect like MySQL, where a bare "Table;mode=update" has no '.' to isolate the suffix.
        var objectAndOptions = destinationObject.Trim().Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var name = objectAndOptions.Length > 0 ? objectAndOptions[0] : string.Empty;
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var queryIndex = name.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            foreach (var option in name[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddOption(options, option);
            }

            name = name[..queryIndex];
        }

        foreach (var option in objectAndOptions.Skip(1))
        {
            AddOption(options, option);
        }

        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var (schema, table) = parts.Length switch
        {
            1 => (DefaultSchema, ValidateIdentifier(parts[0])),
            2 => (ValidateIdentifier(parts[0]), ValidateIdentifier(parts[1])),
            _ => throw new InvalidOperationException("Destination object must be 'Table' or 'Schema.Table'.")
        };

        var mode = options.TryGetValue("mode", out var configuredMode) ? configuredMode.Trim().ToLowerInvariant() : null;
        var upsert = mode == "upsert";
        var updateOnly = mode == "update";

        // Prefer the structured IsUpsertKey flag; fall back to the legacy '?key=' option so destinations configured
        // before the mapping UI grows an IsUpsertKey control (docs/backend/11-destination-schema-ownership-plan.md
        // section 4 item 5) keep working. Remove the fallback once that UI work lands.
        var keyColumn = ResolveUpsertKeyColumn(mappingProfile)
            ?? (options.TryGetValue("key", out var configuredKey) ? ValidateIdentifier(configuredKey) : null);

        if (upsert && keyColumn is null)
        {
            throw new InvalidOperationException(
                $"Destination '{schema}.{table}' is configured for Upsert mode but no mapped field is designated " +
                "as the upsert key. Mark one mapped field's IsUpsertKey in the mapping profile.");
        }

        if (updateOnly && keyColumn is null)
        {
            throw new InvalidOperationException(
                $"Destination '{schema}.{table}' is configured for Update mode but no mapped field is designated " +
                "as the update key. Mark one mapped field's IsUpsertKey in the mapping profile.");
        }

        return new RelationalTarget(schema, table, upsert, updateOnly, keyColumn);
    }

    private static void AddOption(IDictionary<string, string> options, string option)
    {
        var equalsIndex = option.IndexOf('=', StringComparison.Ordinal);
        if (equalsIndex <= 0)
        {
            return;
        }

        options[option[..equalsIndex].Trim()] = option[(equalsIndex + 1)..].Trim();
    }

    /// <summary>
    /// The mapped field marked <see cref="MappingField.IsUpsertKey"/> for this profile's resource/destination-object
    /// scope, if any — derived from the mapping config rather than a second, independently-configured value.
    /// </summary>
    private static string? ResolveUpsertKeyColumn(MappingProfile mappingProfile)
    {
        var keyField = mappingProfile.Fields.FirstOrDefault(field =>
            field.IsUpsertKey &&
            field.IsEnabled &&
            (string.IsNullOrWhiteSpace(field.ResourceType) ||
                string.Equals(field.ResourceType, mappingProfile.ResourceType, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(field.DestinationObject) ||
                string.Equals(field.DestinationObject, mappingProfile.DestinationObject, StringComparison.OrdinalIgnoreCase)));

        return keyField is null ? null : ValidateIdentifier(keyField.TargetField);
    }

    protected static string ValidateIdentifier(string identifier)
    {
        if (!SqlIdentifierRegex().IsMatch(identifier))
        {
            throw new InvalidOperationException($"'{identifier}' is not a valid SQL identifier.");
        }

        return identifier;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex SqlIdentifierRegex();

    private sealed record RelationalTarget(
        string Schema,
        string Table,
        bool Upsert,
        bool UpdateOnly,
        string? KeyColumn);
}
