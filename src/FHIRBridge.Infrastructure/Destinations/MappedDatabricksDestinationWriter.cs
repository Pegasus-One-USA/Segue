using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>Writes mapped records to Databricks through the SQL Statement Execution REST API.</summary>
public sealed class MappedDatabricksDestinationWriter : IConfiguredDestinationWriter
{
    private readonly ISecretProvider _secretProvider;
    private readonly IHttpClientFactory _httpClientFactory;

    public MappedDatabricksDestinationWriter(ISecretProvider secretProvider, IHttpClientFactory httpClientFactory)
    {
        _secretProvider = secretProvider;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<int> WriteAsync(
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return 0;
        }

        var secret = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        var options = DatabricksOptions.Parse(secret);
        var table = ResolveTableName(destination.Target, mappingProfile.DestinationObject, options);
        var statement = BuildInsertStatement(table, records);

        var client = _httpClientFactory.CreateClient(nameof(MappedDatabricksDestinationWriter));
        client.BaseAddress = new Uri(options.WorkspaceUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);

        using var response = await client.PostAsJsonAsync(
            "api/2.0/sql/statements/",
            new StatementRequest(
                options.WarehouseId,
                statement,
                "10s",
                "INLINE",
                new StatementParameters(
                    options.Catalog,
                    options.Schema)),
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return records.Count;
    }

    private static string ResolveTableName(
        string? target,
        string destinationObject,
        DatabricksOptions options)
    {
        var logicalTarget = string.IsNullOrWhiteSpace(target) ? destinationObject : target;
        if (logicalTarget.Contains('.', StringComparison.Ordinal))
        {
            return string.Join('.', logicalTarget.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(EscapeIdentifier));
        }

        return string.Join(
            '.',
            new[] { options.Catalog, options.Schema, logicalTarget }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => EscapeIdentifier(part!)));
    }

    private static string BuildInsertStatement(string tableName, IReadOnlyCollection<MappedDestinationRecord> records)
    {
        var columns = MappedDestinationSerialization.GetColumns(records);
        var escapedColumns = string.Join(", ", columns.Select(EscapeIdentifier));
        var rows = records.Select(record =>
        {
            var values = columns.Select(column => EscapeLiteral(MappedDestinationSerialization.GetCell(record, column)));
            return $"({string.Join(", ", values)})";
        });

        return $"INSERT INTO {tableName} ({escapedColumns}) VALUES {string.Join(", ", rows)}";
    }

    private static string EscapeIdentifier(string identifier)
        => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";

    private static string EscapeLiteral(string value)
        => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private sealed record StatementRequest(
        [property: JsonPropertyName("warehouse_id")] string WarehouseId,
        [property: JsonPropertyName("statement")] string Statement,
        [property: JsonPropertyName("wait_timeout")] string WaitTimeout,
        [property: JsonPropertyName("disposition")] string Disposition,
        [property: JsonPropertyName("parameters")] StatementParameters Parameters);

    private sealed record StatementParameters(
        [property: JsonPropertyName("catalog")] string? Catalog,
        [property: JsonPropertyName("schema")] string? Schema);

    private sealed class DatabricksOptions
    {
        [JsonPropertyName("workspaceUrl")]
        public string WorkspaceUrl { get; init; } = string.Empty;

        [JsonPropertyName("token")]
        public string Token { get; init; } = string.Empty;

        [JsonPropertyName("warehouseId")]
        public string WarehouseId { get; init; } = string.Empty;

        [JsonPropertyName("catalog")]
        public string? Catalog { get; init; }

        [JsonPropertyName("schema")]
        public string? Schema { get; init; }

        public static DatabricksOptions Parse(string secret)
        {
            var options = System.Text.Json.JsonSerializer.Deserialize<DatabricksOptions>(secret)
                ?? throw new InvalidOperationException("Databricks secret must be a JSON object.");

            if (string.IsNullOrWhiteSpace(options.WorkspaceUrl) ||
                string.IsNullOrWhiteSpace(options.Token) ||
                string.IsNullOrWhiteSpace(options.WarehouseId))
            {
                throw new InvalidOperationException(
                    "Databricks secret must include workspaceUrl, token, and warehouseId.");
            }

            return options;
        }
    }
}
