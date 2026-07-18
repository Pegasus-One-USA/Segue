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

        try
        {
            var connectionString = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
            await using var connection = await OpenConnectionAsync(destination.DestinationType, connectionString, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = BuildSelect(destination.DestinationType, schema, table, boundedTop);

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

    private static string Quote(DestinationType type, string id) => type switch
    {
        DestinationType.SqlServer or DestinationType.AzureSql => $"[{id}]",
        DestinationType.MySql => $"`{id}`",
        _ => $"\"{id}\"",
    };

    private static string QualifiedName(DestinationType type, string schema, string table)
        => string.IsNullOrEmpty(schema) ? Quote(type, table) : $"{Quote(type, schema)}.{Quote(type, table)}";

    /// <summary>Splits a destination object ("Table", "schema.Table", optionally with a <c>?mode=…</c> query) into parts.</summary>
    private static (string Schema, string Table) ParseTarget(string destinationObject)
    {
        var name = destinationObject.Trim();
        var queryIndex = name.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            name = name[..queryIndex];
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
