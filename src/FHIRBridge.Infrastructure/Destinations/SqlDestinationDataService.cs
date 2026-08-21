using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Integration.Sql;
using FHIRBridge.SharedKernel.Exceptions;
using MySqlConnector;
using Npgsql;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Reads a capped sample of rows from a relational destination table (SQL Server / Azure SQL / PostgreSQL / MySQL) so
/// the portal can show what a pipeline wrote. Read-only <c>SELECT</c>; identifiers are validated before interpolation
/// and the connection secret is resolved through <see cref="ISecretProvider"/>. Connection/query failures are returned
/// as <see cref="DestinationDataDto.Error"/> rather than thrown, so the UI can render them inline.
/// </summary>
public sealed partial class SqlDestinationDataService : IDestinationDataService
{
    private readonly IConfigurationRepository _repository;
    private readonly ISecretProvider _secretProvider;

    public SqlDestinationDataService(IConfigurationRepository repository, ISecretProvider secretProvider)
    {
        _repository = repository;
        _secretProvider = secretProvider;
    }

    public async Task<DestinationDataDto> ReadSampleAsync(
        Guid destinationId,
        string destinationObject,
        int top,
        IReadOnlyCollection<Guid> pipelineRunIds,
        CancellationToken cancellationToken)
    {
        var destination = await _repository.GetDestinationAsync(destinationId, cancellationToken)
            ?? throw new NotFoundException("DestinationConfiguration", destinationId);

        if (!IsRelational(destination.DestinationType))
        {
            return new DestinationDataDto(destinationObject, [], [], 0,
                $"Destination type '{destination.DestinationType}' is not a relational database — no rows to preview.");
        }

        var target = destination.Target is { Length: > 0 } ? destination.Target : destinationObject;
        string schema, table;
        try
        {
            (schema, table) = ParseTarget(target);
        }
        catch (Exception ex)
        {
            return new DestinationDataDto(target, [], [], 0, ex.Message);
        }

        var boundedTop = Math.Clamp(top, 1, 500);
        var targetName = string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";
        var runIds = pipelineRunIds.ToList();

        try
        {
            var connectionString = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
            await using var connection = await OpenConnectionAsync(destination.DestinationType, connectionString, cancellationToken);

            // Scope the preview to this workflow's own runs when the table carries PipelineRunId; otherwise (a
            // customer-owned table with only mapped business columns) read the whole table — nothing to filter on.
            var runScoped = await HasColumnAsync(connection, schema, table, "PipelineRunId", cancellationToken);
            if (runScoped && runIds.Count == 0)
            {
                return new DestinationDataDto(targetName, [], [], 0,
                    "This workflow hasn't produced any runs yet — nothing has been written to preview.");
            }

            await using var command = connection.CreateCommand();
            command.CommandText = runScoped
                ? BuildSelect(destination.DestinationType, schema, table, boundedTop, runIds.Count)
                : BuildSelect(destination.DestinationType, schema, table, boundedTop);
            if (runScoped)
            {
                for (var i = 0; i < runIds.Count; i++)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = "@runId" + i;
                    parameter.Value = runIds[i];
                    command.Parameters.Add(parameter);
                }
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var rows = new List<IReadOnlyList<string?>>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new string?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                }
                rows.Add(row);
            }

            var qualified = string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";
            return new DestinationDataDto(qualified, columns, rows, rows.Count, null);
        }
        catch (Exception ex)
        {
            return new DestinationDataDto(target, [], [], 0, ex.Message);
        }
    }

    public async Task<DestinationDataDto> ReadByColumnAsync(
        Guid destinationId,
        string destinationObject,
        string columnName,
        string columnValue,
        int top,
        CancellationToken cancellationToken)
    {
        var destination = await _repository.GetDestinationAsync(destinationId, cancellationToken)
            ?? throw new NotFoundException("DestinationConfiguration", destinationId);

        if (!IsRelational(destination.DestinationType))
        {
            return new DestinationDataDto(destinationObject, [], [], 0,
                $"Destination type '{destination.DestinationType}' is not a relational database — no rows to preview.");
        }

        var target = destination.Target is { Length: > 0 } ? destination.Target : destinationObject;
        string schema, table;
        string validatedColumn;
        try
        {
            (schema, table) = ParseTarget(target);
            validatedColumn = ValidateIdentifier(columnName);
        }
        catch (Exception ex)
        {
            return new DestinationDataDto(target, [], [], 0, ex.Message);
        }

        var boundedTop = Math.Clamp(top, 1, 500);

        try
        {
            var connectionString = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
            await using var connection = await OpenConnectionAsync(destination.DestinationType, connectionString, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = BuildColumnSelect(destination.DestinationType, schema, table, validatedColumn, boundedTop);
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@value";
            parameter.Value = columnValue;
            command.Parameters.Add(parameter);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var rows = new List<IReadOnlyList<string?>>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new string?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                }
                rows.Add(row);
            }

            var qualified = string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";
            return new DestinationDataDto(qualified, columns, rows, rows.Count, null);
        }
        catch (Exception ex)
        {
            return new DestinationDataDto(target, [], [], 0, ex.Message);
        }
    }

    private static string BuildColumnSelect(DestinationType type, string schema, string table, string column, int top)
    {
        var qualified = QualifiedName(type, schema, table);
        var where = $"WHERE {Quote(type, column)} = @value";
        return type is DestinationType.SqlServer or DestinationType.AzureSql
            ? $"SELECT TOP {top} * FROM {qualified} {where}"
            : $"SELECT * FROM {qualified} {where} LIMIT {top}";
    }

    private static bool IsRelational(DestinationType type)
        => type is DestinationType.SqlServer or DestinationType.AzureSql or DestinationType.PostgreSql or DestinationType.MySql;

    private static async Task<DbConnection> OpenConnectionAsync(
        DestinationType type,
        string connectionString,
        CancellationToken cancellationToken)
    {
        switch (type)
        {
            case DestinationType.SqlServer:
            case DestinationType.AzureSql:
                return await SqlServerConnectionFactory.OpenConnectionAsync(connectionString, cancellationToken);
            case DestinationType.PostgreSql:
                var npgsql = new NpgsqlConnection(connectionString);
                await npgsql.OpenAsync(cancellationToken);
                return npgsql;
            case DestinationType.MySql:
                var mysql = new MySqlConnection(connectionString);
                await mysql.OpenAsync(cancellationToken);
                return mysql;
            default:
                throw new InvalidOperationException($"Destination type '{type}' is not relational.");
        }
    }

    private static string BuildSelect(DestinationType type, string schema, string table, int top)
    {
        var qualified = QualifiedName(type, schema, table);
        return type is DestinationType.SqlServer or DestinationType.AzureSql
            ? $"SELECT TOP {top} * FROM {qualified}"
            : $"SELECT * FROM {qualified} LIMIT {top}";
    }

    private static string BuildSelect(DestinationType type, string schema, string table, int top, int runIdCount)
    {
        var qualified = QualifiedName(type, schema, table);
        var placeholders = string.Join(", ", Enumerable.Range(0, runIdCount).Select(i => "@runId" + i));
        var where = $"WHERE {Quote(type, "PipelineRunId")} IN ({placeholders})";
        return type is DestinationType.SqlServer or DestinationType.AzureSql
            ? $"SELECT TOP {top} * FROM {qualified} {where}"
            : $"SELECT * FROM {qualified} {where} LIMIT {top}";
    }

    /// <summary>True when the target table declares the given column (INFORMATION_SCHEMA), so we only try to
    /// scope by PipelineRunId on tables that actually have it.</summary>
    private static async Task<bool> HasColumnAsync(
        DbConnection connection,
        string schema,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var sql = "SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @t AND COLUMN_NAME = @c";
        AddParameter(command, "@t", table);
        AddParameter(command, "@c", column);
        if (!string.IsNullOrEmpty(schema))
        {
            sql += " AND TABLE_SCHEMA = @s";
            AddParameter(command, "@s", schema);
        }

        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null and not DBNull;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string Quote(DestinationType type, string id) => type switch
    {
        DestinationType.SqlServer or DestinationType.AzureSql => $"[{id}]",
        DestinationType.MySql => $"`{id}`",
        _ => $"\"{id}\"",
    };

    private static string QualifiedName(DestinationType type, string schema, string table)
        => string.IsNullOrEmpty(schema) ? Quote(type, table) : $"{Quote(type, schema)}.{Quote(type, table)}";

    /// <summary>
    /// Splits a destination object ("Table" / "schema.Table") into parts, first stripping any trailing
    /// write-directive or query suffix. The stored target may carry a <c>;mode=&lt;writeMode&gt;</c> hint
    /// (e.g. "dbo.Patient;mode=upsert") or a <c>?…</c> query string; neither is part of the SQL identifier.
    /// </summary>
    private static (string Schema, string Table) ParseTarget(string destinationObject)
    {
        var name = destinationObject.Trim();
        var suffixIndex = name.IndexOfAny([';', '?']);
        if (suffixIndex >= 0)
        {
            name = name[..suffixIndex];
        }

        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            1 => (string.Empty, ValidateIdentifier(parts[0])),
            2 => (ValidateIdentifier(parts[0]), ValidateIdentifier(parts[1])),
            _ => throw new InvalidOperationException("Destination object must be 'Table' or 'Schema.Table'."),
        };
    }

    private static string ValidateIdentifier(string identifier)
    {
        if (!SqlIdentifierRegex().IsMatch(identifier))
        {
            throw new InvalidOperationException($"'{identifier}' is not a valid SQL identifier.");
        }

        return identifier;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex SqlIdentifierRegex();
}
