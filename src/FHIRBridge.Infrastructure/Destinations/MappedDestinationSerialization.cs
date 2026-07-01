using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations;

internal static class MappedDestinationSerialization
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static object ToPayload(MappedDestinationRecord record)
    {
        return new
        {
            record.TenantId,
            record.PipelineRunId,
            record.ResourceType,
            record.DestinationObject,
            record.SourceResourceId,
            WrittenOnUtc = DateTime.UtcNow,
            Values = record.Values
        };
    }

    public static string ToJson(MappedDestinationRecord record)
    {
        return JsonSerializer.Serialize(ToPayload(record), JsonOptions);
    }

    public static string ToNdjson(IEnumerable<MappedDestinationRecord> records)
    {
        return string.Join(Environment.NewLine, records.Select(ToJson));
    }

    public static async Task PostJsonAsync(
        HttpClient httpClient,
        string endpoint,
        MappedDestinationRecord record,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(endpoint, ToPayload(record), JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public static async Task PutJsonAsync(
        HttpClient httpClient,
        string endpoint,
        string json,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await httpClient.PutAsync(endpoint, content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public static string BuildFileName(DestinationConfiguration destination, MappingProfile mappingProfile, string extension)
    {
        var target = string.IsNullOrWhiteSpace(destination.Target)
            ? mappingProfile.DestinationObject
            : destination.Target;
        var cleanTarget = string.Join("_", target.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

        return $"{cleanTarget}_{DateTime.UtcNow:yyyyMMddHHmmssfff}.{extension.TrimStart('.')}";
    }

    /// <summary>The full ordered column set (standard columns + the union of mapped value keys) for tabular outputs.</summary>
    public static IReadOnlyList<string> GetColumns(IReadOnlyCollection<MappedDestinationRecord> records)
    {
        var valueColumns = records
            .SelectMany(record => record.Values.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(column => column, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new[]
        {
            "TenantId",
            "PipelineRunId",
            "ResourceType",
            "DestinationObject",
            "SourceResourceId",
            "WrittenOnUtc"
        }.Concat(valueColumns).ToList();
    }

    /// <summary>Resolves a single cell value (as string) for a record + column, used by tabular outputs.</summary>
    public static string GetCell(MappedDestinationRecord record, string column)
    {
        var value = column switch
        {
            "TenantId" => record.TenantId,
            "PipelineRunId" => record.PipelineRunId,
            "ResourceType" => record.ResourceType,
            "DestinationObject" => record.DestinationObject,
            "SourceResourceId" => record.SourceResourceId,
            "WrittenOnUtc" => (object?)DateTime.UtcNow.ToString("o"),
            _ => record.Values.TryGetValue(column, out var mappedValue) ? mappedValue : null
        };

        return value?.ToString() ?? string.Empty;
    }

    public static string ToCsv(IReadOnlyCollection<MappedDestinationRecord> records)
    {
        var valueColumns = records
            .SelectMany(record => record.Values.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(column => column, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var columns = new[]
        {
            "TenantId",
            "PipelineRunId",
            "ResourceType",
            "DestinationObject",
            "SourceResourceId",
            "WrittenOnUtc"
        }.Concat(valueColumns).ToList();
        var builder = new StringBuilder();

        builder.AppendLine(string.Join(",", columns.Select(EscapeCsv)));
        foreach (var record in records)
        {
            var row = columns.Select(column =>
            {
                var value = column switch
                {
                    "TenantId" => record.TenantId,
                    "PipelineRunId" => record.PipelineRunId,
                    "ResourceType" => record.ResourceType,
                    "DestinationObject" => record.DestinationObject,
                    "SourceResourceId" => record.SourceResourceId,
                    "WrittenOnUtc" => DateTime.UtcNow,
                    _ => record.Values.TryGetValue(column, out var mappedValue) ? mappedValue : null
                };

                return EscapeCsv(value?.ToString() ?? string.Empty);
            });

            builder.AppendLine(string.Join(",", row));
        }

        return builder.ToString();
    }

    public static async Task WriteTextTargetAsync(
        string targetRootOrUrl,
        string fileName,
        string content,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(targetRootOrUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var endpoint = $"{targetRootOrUrl.TrimEnd('/')}/{Uri.EscapeDataString(fileName)}";
            await PutJsonAsync(httpClient, endpoint, JsonSerializer.Serialize(new { fileName, content }, JsonOptions), cancellationToken);
            return;
        }

        Directory.CreateDirectory(targetRootOrUrl);
        await File.WriteAllTextAsync(Path.Combine(targetRootOrUrl, fileName), content, cancellationToken);
    }

    /// <summary>
    /// Delivers a binary artifact (Parquet/Avro/Protobuf) to either a blob/object store via pre-signed PUT URL
    /// (the raw bytes are PUT to <c>{url}/{fileName}</c> as <c>application/octet-stream</c>) or a local/mounted
    /// directory. Mirrors <see cref="WriteTextTargetAsync"/> for binary payloads.
    /// </summary>
    public static async Task WriteBinaryTargetAsync(
        string targetRootOrUrl,
        string fileName,
        byte[] content,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(targetRootOrUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var endpoint = $"{targetRootOrUrl.TrimEnd('/')}/{Uri.EscapeDataString(fileName)}";
            using var body = new ByteArrayContent(content);
            body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var response = await httpClient.PutAsync(endpoint, body, cancellationToken);
            response.EnsureSuccessStatusCode();
            return;
        }

        Directory.CreateDirectory(targetRootOrUrl);
        await File.WriteAllBytesAsync(Path.Combine(targetRootOrUrl, fileName), content, cancellationToken);
    }

    private static string EscapeCsv(string value)
    {
        if (!value.Contains('"') && !value.Contains(',') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
