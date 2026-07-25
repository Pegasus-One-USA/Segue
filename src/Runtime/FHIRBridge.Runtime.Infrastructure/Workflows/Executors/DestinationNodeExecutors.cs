using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Governance;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using RuntimeDestinationWriteResult = FHIRBridge.Runtime.Application.Workflows.Payloads.DestinationWriteResult;

namespace FHIRBridge.Runtime.Infrastructure.Workflows.Executors;

public sealed class SqlServerDestinationNodeExecutor : DestinationNodeExecutor
{
    public SqlServerDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null)
        : base(WorkflowNodeTypes.SqlServerDestination, DestinationType.SqlServer, writerFactory, workflowDefinitionStore, governanceLogger)
    {
    }
}

public sealed class AzureSqlDestinationNodeExecutor : DestinationNodeExecutor
{
    public AzureSqlDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null)
        : base(WorkflowNodeTypes.AzureSqlDestination, DestinationType.AzureSql, writerFactory, workflowDefinitionStore, governanceLogger)
    {
    }
}

public sealed class BlobDestinationNodeExecutor : DestinationNodeExecutor
{
    public BlobDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null)
        : base(WorkflowNodeTypes.BlobDestination, DestinationType.BlobStorage, writerFactory, workflowDefinitionStore, governanceLogger)
    {
    }
}

public sealed class PowerBiDestinationNodeExecutor : DestinationNodeExecutor
{
    public PowerBiDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null)
        : base(WorkflowNodeTypes.PowerBiDestination, DestinationType.PowerBi, writerFactory, workflowDefinitionStore, governanceLogger)
    {
    }
}

public sealed class PostgreSqlDestinationNodeExecutor : DestinationNodeExecutor
{
    public PostgreSqlDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null)
        : base(WorkflowNodeTypes.PostgreSqlDestination, DestinationType.PostgreSql, writerFactory, workflowDefinitionStore, governanceLogger)
    {
    }
}

public sealed class MySqlDestinationNodeExecutor : DestinationNodeExecutor
{
    public MySqlDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null)
        : base(WorkflowNodeTypes.MySqlDestination, DestinationType.MySql, writerFactory, workflowDefinitionStore, governanceLogger)
    {
    }
}

public sealed class SnowflakeDestinationNodeExecutor : DestinationNodeExecutor
{
    public SnowflakeDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null)
        : base(WorkflowNodeTypes.SnowflakeDestination, DestinationType.Snowflake, writerFactory, workflowDefinitionStore, governanceLogger)
    {
    }
}

public sealed class TableauDestinationNodeExecutor : DestinationNodeExecutor
{
    public TableauDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null)
        : base(WorkflowNodeTypes.TableauDestination, DestinationType.Tableau, writerFactory, workflowDefinitionStore, governanceLogger)
    {
    }
}

public sealed class DatabricksDestinationNodeExecutor : DestinationNodeExecutor
{
    public DatabricksDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null)
        : base(WorkflowNodeTypes.DatabricksDestination, DestinationType.Databricks, writerFactory, workflowDefinitionStore, governanceLogger)
    {
    }
}

public sealed class S3DestinationNodeExecutor : DestinationNodeExecutor
{
    public S3DestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null)
        : base(WorkflowNodeTypes.S3Destination, DestinationType.S3, writerFactory, workflowDefinitionStore, governanceLogger)
    {
    }
}

public sealed class FhirRepositoryDestinationNodeExecutor : DestinationNodeExecutor
{
    public FhirRepositoryDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null)
        : base(WorkflowNodeTypes.FhirRepositoryDestination, DestinationType.FhirRepository, writerFactory, workflowDefinitionStore, governanceLogger)
    {
    }
}

public sealed class CsvDestinationNodeExecutor : DestinationNodeExecutor
{
    public CsvDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null)
        : base(WorkflowNodeTypes.CsvDestination, DestinationType.Csv, writerFactory, workflowDefinitionStore, governanceLogger)
    {
    }
}

public sealed class ExcelDestinationNodeExecutor : DestinationNodeExecutor
{
    public ExcelDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null)
        : base(WorkflowNodeTypes.ExcelDestination, DestinationType.Excel, writerFactory, workflowDefinitionStore, governanceLogger)
    {
    }
}

public sealed class NdjsonDestinationNodeExecutor : DestinationNodeExecutor
{
    public NdjsonDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null)
        : base(WorkflowNodeTypes.NdjsonDestination, DestinationType.Ndjson, writerFactory, workflowDefinitionStore, governanceLogger)
    {
    }
}

public sealed class ParquetDestinationNodeExecutor : DestinationNodeExecutor
{
    public ParquetDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null)
        : base(WorkflowNodeTypes.ParquetDestination, DestinationType.Parquet, writerFactory, workflowDefinitionStore, governanceLogger)
    {
    }
}

public sealed class AvroDestinationNodeExecutor : DestinationNodeExecutor
{
    public AvroDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null)
        : base(WorkflowNodeTypes.AvroDestination, DestinationType.Avro, writerFactory, workflowDefinitionStore, governanceLogger)
    {
    }
}

public sealed class ProtobufDestinationNodeExecutor : DestinationNodeExecutor
{
    public ProtobufDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null)
        : base(WorkflowNodeTypes.ProtobufDestination, DestinationType.Protobuf, writerFactory, workflowDefinitionStore, governanceLogger)
    {
    }
}

public sealed class PdfDestinationNodeExecutor : DestinationNodeExecutor
{
    public PdfDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null)
        : base(WorkflowNodeTypes.PdfDestination, DestinationType.Pdf, writerFactory, workflowDefinitionStore, governanceLogger)
    {
    }
}

public sealed class SftpDestinationNodeExecutor : DestinationNodeExecutor
{
    public SftpDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null)
        : base(WorkflowNodeTypes.SftpDestination, DestinationType.Sftp, writerFactory, workflowDefinitionStore, governanceLogger)
    {
    }
}

public sealed class RestApiDestinationNodeExecutor : DestinationNodeExecutor
{
    public RestApiDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null)
        : base(WorkflowNodeTypes.RestApiDestination, DestinationType.RestApi, writerFactory, workflowDefinitionStore, governanceLogger)
    {
    }
}

public sealed class InMemoryDestinationNodeExecutor : DestinationNodeExecutor
{
    public InMemoryDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null)
        : base(WorkflowNodeTypes.InMemoryDestination, DestinationType.InMemory, writerFactory, workflowDefinitionStore, governanceLogger)
    {
    }
}

/// <summary>Phase 2 example node: consumes an upstream destination's <see cref="RuntimeDestinationWriteResult"/> (not
/// fresh mapped records) and notifies a webhook that the write completed. Not currently exposed in the catalog
/// (see the "GATED" comment in DefaultWorkflowNodeCatalog.cs) — implemented and DI-registered so it's ready to
/// re-list once this capability is actually in scope, matching the same gating pattern already used for the other
/// unlisted destination writers. See docs/backend/05-workflow-node-checkpoints-plan.md §4.2.</summary>
public sealed class WebhookNotifierNodeExecutor : WorkflowNodeExecutorBase
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ISecretProvider? _secretProvider;
    private readonly IHttpClientFactory? _httpClientFactory;

    public WebhookNotifierNodeExecutor(ISecretProvider? secretProvider = null, IHttpClientFactory? httpClientFactory = null)
        : base(WorkflowNodeTypes.WebhookNotifier, WorkflowDataContract.DestinationWriteResult)
    {
        _secretProvider = secretProvider;
        _httpClientFactory = httpClientFactory;
    }

    public override async Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        var upstreamWrite = inputs.Select(input => input.Payload).OfType<RuntimeDestinationWriteResult>().FirstOrDefault();
        var config = ReadWebhookConfiguration(node);
        var result = new RuntimeDestinationWriteResult(
            upstreamWrite?.DestinationId ?? node.Id.ToString("N"),
            upstreamWrite?.RecordsWritten ?? 0,
            DateTimeOffset.UtcNow);

        var metadata = new Dictionary<string, object?> { ["executor"] = nameof(WebhookNotifierNodeExecutor) };
        if (config.IncludeRecordLevelData)
        {
            metadata["warning"] = "includeRecordLevelData is not supported — record-level data is never forwarded to a webhook, to protect PHI.";
        }

        if (string.IsNullOrWhiteSpace(config.WebhookUrl) || _httpClientFactory is null)
        {
            metadata["delivered"] = false;
            metadata["reason"] = "No webhook URL configured.";
            return new WorkflowNodeOutput(node.Id, node.NodeType, result, OutputContract, metadata);
        }

        var payloadJson = BuildPayloadJson(config, context, upstreamWrite);
        var httpClient = _httpClientFactory.CreateClient(nameof(WebhookNotifierNodeExecutor));
        httpClient.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);

        var (delivered, statusCode, attempts, error) = await SendWithRetryAsync(
            httpClient, config, context, node, payloadJson, cancellationToken);

        metadata["delivered"] = delivered;
        metadata["attempts"] = attempts;
        if (statusCode is { } code)
        {
            metadata["httpStatusCode"] = code;
        }
        if (error is not null)
        {
            metadata["error"] = error;
        }

        if (!delivered && config.OnFailure == WebhookOnFailure.Fail)
        {
            throw new InvalidOperationException(
                $"Webhook notification to '{config.WebhookUrl}' failed after {attempts} attempt(s): {error}");
        }

        return new WorkflowNodeOutput(node.Id, node.NodeType, result, OutputContract, metadata);
    }

    protected override object CreatePayload(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs)
    {
        var upstreamWrite = inputs.Select(input => input.Payload).OfType<RuntimeDestinationWriteResult>().FirstOrDefault();
        return new RuntimeDestinationWriteResult(
            upstreamWrite?.DestinationId ?? node.Id.ToString("N"),
            upstreamWrite?.RecordsWritten ?? 0,
            DateTimeOffset.UtcNow);
    }

    private async Task<(bool Delivered, int? StatusCode, int Attempts, string? Error)> SendWithRetryAsync(
        HttpClient httpClient,
        WebhookConfiguration config,
        WorkflowExecutionContext context,
        WorkflowNode node,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(1, config.RetryCount + 1);
        string? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(new HttpMethod(config.HttpMethod), config.WebhookUrl)
                {
                    Content = new StringContent(payloadJson, Encoding.UTF8, config.ContentType),
                };
                request.Headers.TryAddWithoutValidation("X-Idempotency-Key", context.WorkflowRunId.ToString("N"));
                request.Headers.TryAddWithoutValidation("X-FHIRBridge-Node-Id", node.Id.ToString("N"));
                foreach (var header in config.Headers)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                await ApplyAuthAsync(request, config, payloadJson, cancellationToken);

                using var response = await httpClient.SendAsync(request, cancellationToken);
                var success = config.ExpectedStatusCodes.Count > 0
                    ? config.ExpectedStatusCodes.Contains((int)response.StatusCode)
                    : response.IsSuccessStatusCode;

                if (success)
                {
                    return (true, (int)response.StatusCode, attempt, null);
                }

                lastError = $"HTTP {(int)response.StatusCode}";
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                lastError = exception.Message;
            }

            if (attempt < maxAttempts)
            {
                var backoff = TimeSpan.FromSeconds(config.RetryBackoffSeconds * Math.Pow(2, attempt - 1));
                await Task.Delay(backoff, cancellationToken);
            }
        }

        return (false, null, maxAttempts, lastError);
    }

    private async Task ApplyAuthAsync(
        HttpRequestMessage request, WebhookConfiguration config, string payloadJson, CancellationToken cancellationToken)
    {
        if (config.AuthType == WebhookAuthType.None || _secretProvider is null)
        {
            return;
        }

        var secret = await _secretProvider.GetSecretAsync(
            new SecretReference(config.AuthSecretKeyVaultName ?? string.Empty, config.AuthSecretName ?? string.Empty),
            cancellationToken);

        switch (config.AuthType)
        {
            case WebhookAuthType.Bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                break;
            case WebhookAuthType.ApiKeyHeader:
                request.Headers.TryAddWithoutValidation(config.AuthHeaderName ?? "X-Api-Key", secret);
                break;
            case WebhookAuthType.Basic:
                // Secret is expected to be stored pre-formatted as "username:password".
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)));
                break;
            case WebhookAuthType.Hmac:
                using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
                {
                    var signature = Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadJson)));
                    request.Headers.TryAddWithoutValidation(config.AuthHeaderName ?? "X-Signature-256", signature);
                }
                break;
        }
    }

    private static string BuildPayloadJson(
        WebhookConfiguration config, WorkflowExecutionContext context, RuntimeDestinationWriteResult? upstreamWrite)
    {
        if (config.PayloadTemplate == WebhookPayloadTemplate.Custom
            && !string.IsNullOrWhiteSpace(config.CustomPayloadTemplate))
        {
            return RenderCustomTemplate(config.CustomPayloadTemplate, context, upstreamWrite);
        }

        object payload = config.PayloadTemplate == WebhookPayloadTemplate.RunPing
            ? new { workflowRunId = context.WorkflowRunId, status = "completed" }
            : new
            {
                destinationId = upstreamWrite?.DestinationId,
                recordsWritten = upstreamWrite?.RecordsWritten ?? 0,
                writtenAtUtc = upstreamWrite?.WrittenAt ?? DateTimeOffset.UtcNow,
                workflowRunId = context.WorkflowRunId,
            };

        return JsonSerializer.Serialize(payload, PayloadJsonOptions);
    }

    private static string RenderCustomTemplate(
        string template, WorkflowExecutionContext context, RuntimeDestinationWriteResult? upstreamWrite)
        => template
            .Replace("{{destinationId}}", upstreamWrite?.DestinationId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{recordsWritten}}", (upstreamWrite?.RecordsWritten ?? 0).ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{{workflowRunId}}", context.WorkflowRunId.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{{timestampUtc}}", DateTimeOffset.UtcNow.ToString("O"), StringComparison.OrdinalIgnoreCase);

    private static WebhookConfiguration ReadWebhookConfiguration(WorkflowNode node)
    {
        var authType = Enum.TryParse<WebhookAuthType>(ReadStringConfiguration(node, "authType"), true, out var parsedAuth)
            ? parsedAuth
            : WebhookAuthType.None;
        var payloadTemplate = Enum.TryParse<WebhookPayloadTemplate>(ReadStringConfiguration(node, "payloadTemplate"), true, out var parsedTemplate)
            ? parsedTemplate
            : WebhookPayloadTemplate.WriteSummary;
        var onFailure = Enum.TryParse<WebhookOnFailure>(ReadStringConfiguration(node, "onFailure"), true, out var parsedOnFailure)
            ? parsedOnFailure
            : WebhookOnFailure.Fail;

        var expectedStatusCodes = (ReadStringConfiguration(node, "expectedStatusCodes") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(code => int.TryParse(code, out var parsed) ? parsed : (int?)null)
            .Where(code => code is not null)
            .Select(code => code!.Value)
            .ToArray();

        var headers = ReadConfiguration<Dictionary<string, string>>(node, "headers") ?? new Dictionary<string, string>();

        return new WebhookConfiguration(
            WebhookUrl: ReadStringConfiguration(node, "webhookUrl"),
            HttpMethod: ReadStringConfiguration(node, "httpMethod") ?? "POST",
            ContentType: ReadStringConfiguration(node, "contentType") ?? "application/json",
            TimeoutSeconds: ReadIntConfiguration(node, "timeoutSeconds") ?? 30,
            AuthType: authType,
            AuthSecretKeyVaultName: ReadStringConfiguration(node, "authSecretKeyVaultName"),
            AuthSecretName: ReadStringConfiguration(node, "authSecretName"),
            AuthHeaderName: ReadStringConfiguration(node, "authHeaderName"),
            PayloadTemplate: payloadTemplate,
            CustomPayloadTemplate: ReadStringConfiguration(node, "customPayloadTemplate"),
            // Portal-authored node config is a flat string dictionary (BaseNode.fields: Record<string,string>), so a
            // checkbox value round-trips as the JSON string "true"/"false", not a genuine JSON boolean — read it as a
            // string and parse leniently rather than relying on ReadBoolConfiguration's strict JsonValueKind check.
            IncludeRecordLevelData: bool.TryParse(ReadStringConfiguration(node, "includeRecordLevelData"), out var includeRecords) && includeRecords,
            RetryCount: ReadIntConfiguration(node, "retryCount") ?? 0,
            RetryBackoffSeconds: ReadIntConfiguration(node, "retryBackoffSeconds") ?? 2,
            ExpectedStatusCodes: expectedStatusCodes,
            OnFailure: onFailure,
            Headers: headers);
    }

    private static int? ReadIntConfiguration(WorkflowNode node, string propertyName)
        => int.TryParse(ReadStringConfiguration(node, propertyName), out var value) ? value : null;

    private enum WebhookAuthType { None, Bearer, ApiKeyHeader, Basic, Hmac }

    private enum WebhookPayloadTemplate { WriteSummary, RunPing, Custom }

    private enum WebhookOnFailure { Fail, BestEffort }

    private sealed record WebhookConfiguration(
        string? WebhookUrl,
        string HttpMethod,
        string ContentType,
        int TimeoutSeconds,
        WebhookAuthType AuthType,
        string? AuthSecretKeyVaultName,
        string? AuthSecretName,
        string? AuthHeaderName,
        WebhookPayloadTemplate PayloadTemplate,
        string? CustomPayloadTemplate,
        bool IncludeRecordLevelData,
        int RetryCount,
        int RetryBackoffSeconds,
        IReadOnlyCollection<int> ExpectedStatusCodes,
        WebhookOnFailure OnFailure,
        IReadOnlyDictionary<string, string> Headers);
}

public abstract class DestinationNodeExecutor : WorkflowNodeExecutorBase
{
    private readonly DestinationType _destinationType;
    private readonly IConfiguredDestinationWriterFactory? _writerFactory;
    private readonly IWorkflowDefinitionStore? _workflowDefinitionStore;
    private readonly IGovernanceLogger? _governanceLogger;

    protected DestinationNodeExecutor(
        string nodeType,
        DestinationType destinationType,
        IConfiguredDestinationWriterFactory? writerFactory,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null)
        : base(nodeType, WorkflowDataContract.DestinationWriteResult)
    {
        _destinationType = destinationType;
        _writerFactory = writerFactory;
        _workflowDefinitionStore = workflowDefinitionStore;
        _governanceLogger = governanceLogger;
    }

    public override async Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        var records = PassThroughNodeExecutor.ReadMappedRecords(inputs).ToArray();
        var destination = ReadConfiguration<DestinationConfiguration>(node, "destination")
            ?? CreateDestinationConfiguration(context, node);
        var mappingProfile = ReadConfiguration<MappingProfile>(node, "mappingProfile")
            ?? CreateMappingProfile(context, node, records);

        if (_writerFactory is null)
        {
            return await base.ExecuteAsync(context, node, inputs, cancellationToken);
        }

        var writer = _writerFactory.Create(_destinationType);
        // The Runtime DAG engine has no HTTP response to carry Download-mode bytes back through — this run always
        // executes as a background node, not a synchronous API call — so inline delivery is never allowed here.
        // RouteName drives both the email {{RouteName}} template placeholder and (for CSV) the multi-resource ZIP
        // filename — the workflow's own name is far more useful here than the generic node type string.
        var workflowName = await ResolveWorkflowNameAsync(node, cancellationToken) ?? node.NodeType;
        var writeContext = new PipelineWriteContext(
            AllowInlineDelivery: false,
            workflowName,
            DateTimeOffset.UtcNow);
        var writeResult = await writer.WriteAsync(destination, mappingProfile, records, writeContext, cancellationToken);
        var written = writeResult.Count;
        var result = new RuntimeDestinationWriteResult(
            destination.Id.ToString("N"), written, DateTimeOffset.UtcNow);

        // Per docs/ERRORS_SCREEN_CATEGORIZATION_ANALYSIS.md discussion: Operations → Exports previously only ever
        // reflected the Configured Pipeline plane (ConfiguredPipelineService's own LogExportAsync call) — a
        // destination write completed by this engine (the only one with an authoring UI) was invisible there no
        // matter how many workflows successfully wrote data out. Logging here too means "Exports" reflects every
        // engine's destination writes consistently, not just one of them.
        if (_governanceLogger is not null)
        {
            await _governanceLogger.LogExportAsync(
                new ExportEntry(
                    destination.Name,
                    _destinationType.ToString(),
                    written,
                    written > 0 ? "Succeeded" : "NoData",
                    writeResult.InlineDownload?.Content.Length,
                    context.WorkflowRunId,
                    context.CorrelationId),
                cancellationToken);
        }

        return new WorkflowNodeOutput(
            node.Id,
            node.NodeType,
            result,
            WorkflowDataContract.DestinationWriteResult,
            new Dictionary<string, object?>
            {
                ["executor"] = GetType().Name,
                ["destinationType"] = _destinationType.ToString(),
                ["recordsWritten"] = written,
                // Populated only for a CSV destination using Download-URL delivery — the caller of /run reads this
                // back to fetch the generated file. Download (inline-bytes) delivery is not supported on this engine
                // (see the AllowInlineDelivery comment above) and will have already thrown before reaching here.
                ["downloadUrl"] = writeResult.DownloadUrl
            });
    }

    protected override object CreatePayload(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs)
    {
        var destinationId = node.Configuration.FirstOrDefault(configuration =>
                string.Equals(configuration.Key, "destinationId", StringComparison.OrdinalIgnoreCase))
            ?.Value
            ?? node.Id.ToString("N");

        return new RuntimeDestinationWriteResult(destinationId, inputs.Count, DateTimeOffset.UtcNow);
    }

    private async Task<string?> ResolveWorkflowNameAsync(WorkflowNode node, CancellationToken cancellationToken)
    {
        if (_workflowDefinitionStore is null)
        {
            return null;
        }

        var definition = await _workflowDefinitionStore.GetAsync(node.WorkflowDefinitionId, cancellationToken);
        return definition?.Name;
    }

    private DestinationConfiguration CreateDestinationConfiguration(WorkflowExecutionContext context, WorkflowNode node)
    {
        var target = ReadStringConfiguration(node, "target");
        // Rebuild the secret reference the projection embedded (vault + name only). The writer resolves the actual
        // connection secret via ISecretProvider, so a graph-driven run can write to a secret-backed destination.
        var keyVaultName = ReadStringConfiguration(node, "secretKeyVaultName") ?? string.Empty;
        var secretName = ReadStringConfiguration(node, "secretName") ?? string.Empty;
        return new DestinationConfiguration(
            node.DisplayName,
            _destinationType,
            new SecretReference(keyVaultName, secretName),
            target,
            BuildConnectionMetadataJson(node));
    }

    // The wizard stores a destination node's dest_* fields (delivery mode, email/SFTP/download-link settings, ...)
    // as flat top-level properties on the node's own ConfigurationJson — the same shape
    // DestinationConfiguration.ConnectionMetadataJson expects. Without this, a graph-driven run reconstructs the
    // destination with ConnectionMetadataJson permanently null, silently losing every dest_* setting (e.g. a CSV
    // destination's delivery mode always defaulting to Download regardless of what was actually configured).
    private static string? BuildConnectionMetadataJson(WorkflowNode node)
    {
        if (string.IsNullOrWhiteSpace(node.ConfigurationJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(node.ConfigurationJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var metadata = new Dictionary<string, JsonElement>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.StartsWith("dest_", StringComparison.Ordinal))
            {
                metadata[property.Name] = property.Value.Clone();
            }
        }

        return metadata.Count == 0 ? null : JsonSerializer.Serialize(metadata, JsonOptions);
    }

    private static MappingProfile CreateMappingProfile(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<MappedDestinationRecord> records)
    {
        var resourceType = ReadStringConfiguration(node, "resourceType")
            ?? records.FirstOrDefault()?.ResourceType
            ?? "Patient";
        var destinationObject = ReadStringConfiguration(node, "destinationObject") ?? resourceType;

        // The writer creates/aligns the target table's columns from these fields, so carry them from node config
        // (the projection embeds the same fields the mapping node used) rather than defaulting to none.
        var fields = (ReadConfiguration<IReadOnlyCollection<MappingFieldDto>>(node, "fields") ?? [])
            .Select(ConfigurationMapper.ToDomain)
            .ToList();

        return new MappingProfile(
            node.DisplayName,
            resourceType,
            Guid.Empty,
            Guid.Empty,
            destinationObject,
            fields);
    }
}
