using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Provider-agnostic relational destination writer built on ADO.NET (<see cref="DbConnection"/>). The customer owns
/// the destination schema: the target table must already exist and every written column must be one the mapping
/// profile explicitly maps. Writes mapped records in Insert or Upsert mode. Upsert requires an explicit
/// '?key=&lt;ColumnName&gt;' option naming one of the mapped columns, and is implemented as a portable delete-by-key
/// + insert within a transaction (last-write-wins) so it works on any dialect without requiring a pre-existing
/// unique index. Concrete subclasses supply the dialect (connection, identifier quoting, type mapping,
/// table-existence check). Mirrors <see cref="MappedSqlServerDestinationWriter"/> for SQL Server / Azure SQL.
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
        var target = ParseTarget(destination.Target ?? mappingProfile.DestinationObject);

        await using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await EnsureTableAsync(connection, target, mappingProfile, cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var record in records)
        {
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

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@" + name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    // The portable relational writer stores every value as text so inserts never fail on cross-dialect type coercion.
    private static object Stringify(object? value)
        => value is null ? DBNull.Value : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static bool TryGetKeyValue(MappedDestinationRecord record, string keyColumn, out object? keyValue)
        => record.Values.TryGetValue(keyColumn, out keyValue) && keyValue is not null;

    private RelationalTarget ParseTarget(string destinationObject)
    {
        var name = destinationObject.Trim();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var queryIndex = name.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            foreach (var option in name[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var equalsIndex = option.IndexOf('=', StringComparison.Ordinal);
                if (equalsIndex > 0)
                {
                    options[option[..equalsIndex].Trim()] = option[(equalsIndex + 1)..].Trim();
                }
            }

            name = name[..queryIndex];
        }

        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var (schema, table) = parts.Length switch
        {
            1 => (DefaultSchema, ValidateIdentifier(parts[0])),
            2 => (ValidateIdentifier(parts[0]), ValidateIdentifier(parts[1])),
            _ => throw new InvalidOperationException("Destination object must be 'Table' or 'Schema.Table'.")
        };

        var upsert = options.TryGetValue("mode", out var mode) && string.Equals(mode, "upsert", StringComparison.OrdinalIgnoreCase);
        var keyColumn = options.TryGetValue("key", out var key) ? ValidateIdentifier(key) : null;

        if (upsert && keyColumn is null)
        {
            throw new InvalidOperationException(
                $"Destination '{schema}.{table}' is configured for Upsert mode but has no '?key=<ColumnName>' " +
                "option naming which mapped column identifies an existing row.");
        }

        return new RelationalTarget(schema, table, upsert, keyColumn);
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
        string? KeyColumn);
}
