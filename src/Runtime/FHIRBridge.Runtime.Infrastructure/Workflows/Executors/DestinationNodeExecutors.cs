using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Persistence;
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
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.SqlServerDestination, DestinationType.SqlServer, writerFactory, workflowDefinitionStore, governanceLogger, configurationRepository)
    {
    }
}

public sealed class AzureSqlDestinationNodeExecutor : DestinationNodeExecutor
{
    public AzureSqlDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.AzureSqlDestination, DestinationType.AzureSql, writerFactory, workflowDefinitionStore, governanceLogger, configurationRepository)
    {
    }
}

public sealed class BlobDestinationNodeExecutor : DestinationNodeExecutor
{
    public BlobDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.BlobDestination, DestinationType.BlobStorage, writerFactory, workflowDefinitionStore, governanceLogger, configurationRepository)
    {
    }
}

public sealed class PowerBiDestinationNodeExecutor : DestinationNodeExecutor
{
    public PowerBiDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.PowerBiDestination, DestinationType.PowerBi, writerFactory, workflowDefinitionStore, governanceLogger, configurationRepository)
    {
    }
}

public sealed class PostgreSqlDestinationNodeExecutor : DestinationNodeExecutor
{
    public PostgreSqlDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.PostgreSqlDestination, DestinationType.PostgreSql, writerFactory, workflowDefinitionStore, governanceLogger, configurationRepository)
    {
    }
}

public sealed class MySqlDestinationNodeExecutor : DestinationNodeExecutor
{
    public MySqlDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.MySqlDestination, DestinationType.MySql, writerFactory, workflowDefinitionStore, governanceLogger, configurationRepository)
    {
    }
}

public sealed class SnowflakeDestinationNodeExecutor : DestinationNodeExecutor
{
    public SnowflakeDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.SnowflakeDestination, DestinationType.Snowflake, writerFactory, workflowDefinitionStore, governanceLogger, configurationRepository)
    {
    }
}

public sealed class TableauDestinationNodeExecutor : DestinationNodeExecutor
{
    public TableauDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.TableauDestination, DestinationType.Tableau, writerFactory, workflowDefinitionStore, governanceLogger, configurationRepository)
    {
    }
}

public sealed class DatabricksDestinationNodeExecutor : DestinationNodeExecutor
{
    public DatabricksDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.DatabricksDestination, DestinationType.Databricks, writerFactory, workflowDefinitionStore, governanceLogger, configurationRepository)
    {
    }
}

public sealed class S3DestinationNodeExecutor : DestinationNodeExecutor
{
    public S3DestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.S3Destination, DestinationType.S3, writerFactory, workflowDefinitionStore, governanceLogger, configurationRepository)
    {
    }
}

public sealed class MongoDestinationNodeExecutor : DestinationNodeExecutor
{
    public MongoDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.MongoDestination, DestinationType.Mongo, writerFactory, workflowDefinitionStore, governanceLogger, configurationRepository)
    {
    }
}

public sealed class FhirRepositoryDestinationNodeExecutor : DestinationNodeExecutor
{
    public FhirRepositoryDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null,
        FHIRBridge.Runtime.Application.Abstractions.Connectors.IFhirSourceClientFactory? sourceClientFactory = null,
        FHIRBridge.Runtime.Application.Abstractions.Sources.ISourceConnectionRuntimeResolver? sourceConnectionResolver = null)
        : base(WorkflowNodeTypes.FhirRepositoryDestination, DestinationType.FhirRepository, writerFactory, workflowDefinitionStore, governanceLogger, configurationRepository, sourceClientFactory, sourceConnectionResolver)
    {
    }
}

public sealed class CsvDestinationNodeExecutor : DestinationNodeExecutor
{
    public CsvDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.CsvDestination, DestinationType.Csv, writerFactory, workflowDefinitionStore, governanceLogger, configurationRepository)
    {
    }
}

public sealed class ExcelDestinationNodeExecutor : DestinationNodeExecutor
{
    public ExcelDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.ExcelDestination, DestinationType.Excel, writerFactory, workflowDefinitionStore, governanceLogger, configurationRepository)
    {
    }
}

public sealed class NdjsonDestinationNodeExecutor : DestinationNodeExecutor
{
    public NdjsonDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.NdjsonDestination, DestinationType.Ndjson, writerFactory, workflowDefinitionStore, governanceLogger, configurationRepository)
    {
    }
}

public sealed class ParquetDestinationNodeExecutor : DestinationNodeExecutor
{
    public ParquetDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.ParquetDestination, DestinationType.Parquet, writerFactory, workflowDefinitionStore, governanceLogger, configurationRepository)
    {
    }
}

public sealed class AvroDestinationNodeExecutor : DestinationNodeExecutor
{
    public AvroDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.AvroDestination, DestinationType.Avro, writerFactory, workflowDefinitionStore, governanceLogger, configurationRepository)
    {
    }
}

public sealed class ProtobufDestinationNodeExecutor : DestinationNodeExecutor
{
    public ProtobufDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.ProtobufDestination, DestinationType.Protobuf, writerFactory, workflowDefinitionStore, governanceLogger, configurationRepository)
    {
    }
}

public sealed class PdfDestinationNodeExecutor : DestinationNodeExecutor
{
    public PdfDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.PdfDestination, DestinationType.Pdf, writerFactory, workflowDefinitionStore, governanceLogger, configurationRepository)
    {
    }
}

public sealed class SftpDestinationNodeExecutor : DestinationNodeExecutor
{
    public SftpDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.SftpDestination, DestinationType.Sftp, writerFactory, workflowDefinitionStore, governanceLogger, configurationRepository)
    {
    }
}

public sealed class RestApiDestinationNodeExecutor : DestinationNodeExecutor
{
    public RestApiDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.RestApiDestination, DestinationType.RestApi, writerFactory, workflowDefinitionStore, governanceLogger, configurationRepository)
    {
    }
}

public sealed class InMemoryDestinationNodeExecutor : DestinationNodeExecutor
{
    public InMemoryDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.InMemoryDestination, DestinationType.InMemory, writerFactory, workflowDefinitionStore, governanceLogger, configurationRepository)
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
    // Destination types where each resource type in a mixed batch normally targets its OWN table/collection/blob
    // (Patient -> dbo.Patient_New, Condition -> dbo.Condition, ...). Only these route a mixed batch per resource
    // type at write time (see ExecuteAsync) — every other destination type keeps writing the whole batch in one
    // call, unchanged, since e.g. a CSV writer already groups multi-resource output itself (a multi-resource ZIP)
    // and splitting the call here would silently break that.
    // BlobStorage is included here (not just relational/Mongo) because MappedBlobStorageDestinationWriter does
    // NOT group by resource type internally — without this, a mixed batch reaching a Blob node in one call would
    // land in a single blob whose metadata falsely claims only one resource type.
    private static readonly HashSet<DestinationType> MultiTableRelationalDestinationTypes =
    [
        DestinationType.SqlServer,
        DestinationType.AzureSql,
        DestinationType.MySql,
        DestinationType.PostgreSql,
        DestinationType.Snowflake,
        DestinationType.Mongo,
        DestinationType.BlobStorage,
    ];

    // NodeType -> RuntimeSourceType for every source node executor's own hardcoded mapping (see SourceNodeExecutors.cs
    // constructors) — duplicated here rather than shared, since this is the one place outside those constructors
    // that needs to go the other direction (given a source NODE found by graph walk, which client type to build).
    private static readonly Dictionary<string, FHIRBridge.Runtime.Domain.Enums.RuntimeSourceType> SourceNodeTypeByNodeType =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [WorkflowNodeTypes.EpicSource] = FHIRBridge.Runtime.Domain.Enums.RuntimeSourceType.Epic,
            [WorkflowNodeTypes.CernerSource] = FHIRBridge.Runtime.Domain.Enums.RuntimeSourceType.Cerner,
            [WorkflowNodeTypes.EClinicalWorksSource] = FHIRBridge.Runtime.Domain.Enums.RuntimeSourceType.Healow,
            [WorkflowNodeTypes.AthenahealthSource] = FHIRBridge.Runtime.Domain.Enums.RuntimeSourceType.GenericFhir,
            [WorkflowNodeTypes.AllscriptsSource] = FHIRBridge.Runtime.Domain.Enums.RuntimeSourceType.Allscripts,
            [WorkflowNodeTypes.MeditechSource] = FHIRBridge.Runtime.Domain.Enums.RuntimeSourceType.MeditechGreenfield,
            [WorkflowNodeTypes.GenericFhirSource] = FHIRBridge.Runtime.Domain.Enums.RuntimeSourceType.GenericFhir,
            [WorkflowNodeTypes.SampleSource] = FHIRBridge.Runtime.Domain.Enums.RuntimeSourceType.Sample,
        };

    private readonly DestinationType _destinationType;
    private readonly IConfiguredDestinationWriterFactory? _writerFactory;
    private readonly IWorkflowDefinitionStore? _workflowDefinitionStore;
    private readonly IGovernanceLogger? _governanceLogger;
    private readonly IConfigurationRepository? _configurationRepository;
    private readonly FHIRBridge.Runtime.Application.Abstractions.Connectors.IFhirSourceClientFactory? _sourceClientFactory;
    private readonly FHIRBridge.Runtime.Application.Abstractions.Sources.ISourceConnectionRuntimeResolver? _sourceConnectionResolver;

    protected DestinationNodeExecutor(
        string nodeType,
        DestinationType destinationType,
        IConfiguredDestinationWriterFactory? writerFactory,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null,
        FHIRBridge.Runtime.Application.Abstractions.Connectors.IFhirSourceClientFactory? sourceClientFactory = null,
        FHIRBridge.Runtime.Application.Abstractions.Sources.ISourceConnectionRuntimeResolver? sourceConnectionResolver = null)
        : base(nodeType, WorkflowDataContract.DestinationWriteResult)
    {
        _destinationType = destinationType;
        _writerFactory = writerFactory;
        _workflowDefinitionStore = workflowDefinitionStore;
        _governanceLogger = governanceLogger;
        _configurationRepository = configurationRepository;
        _sourceClientFactory = sourceClientFactory;
        _sourceConnectionResolver = sourceConnectionResolver;
    }

    /// <summary>
    /// Builds <see cref="PipelineWriteContext.FetchMissingReferenceAsync"/> for <c>MappedFhirRepositoryDestinationWriter</c>'s
    /// opt-in <c>dest_autoFetchMissingReferences</c> — closes over the exact one source node feeding this destination
    /// in the workflow graph. Deliberately returns null (the writer falls back to today's blocking behavior,
    /// unchanged) when: no source-side dependencies were injected for this destination type (every destination type
    /// other than FhirRepository never passes them — see the constructor); the graph has zero or more than one
    /// upstream source node (never guess which one to use); that source node's own type isn't one this executor
    /// knows how to build a client for; or its <c>sourceConnectionId</c> doesn't parse. Ambiguity anywhere in this
    /// resolution intentionally falls back to null rather than picking arbitrarily.
    /// </summary>
    private async Task<Func<string, string, CancellationToken, Task<string?>>?> ResolveFetchMissingReferenceDelegateAsync(
        WorkflowNode node,
        CancellationToken cancellationToken)
    {
        if (_sourceClientFactory is null || _sourceConnectionResolver is null || _workflowDefinitionStore is null)
        {
            return null;
        }

        var definition = await _workflowDefinitionStore.GetAsync(node.WorkflowDefinitionId, cancellationToken);
        if (definition is null)
        {
            return null;
        }

        // Walk edges backward from this destination node to find every upstream node reachable from it.
        var upstream = new HashSet<Guid>();
        var frontier = new Queue<Guid>();
        frontier.Enqueue(node.Id);
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var edge in definition.Edges.Where(e => e.ToNodeId == current))
            {
                if (upstream.Add(edge.FromNodeId))
                {
                    frontier.Enqueue(edge.FromNodeId);
                }
            }
        }

        var sourceNodes = definition.Nodes
            .Where(candidate => upstream.Contains(candidate.Id) && candidate.Category == WorkflowNodeCategory.Source)
            .ToList();

        if (sourceNodes.Count != 1 || !SourceNodeTypeByNodeType.TryGetValue(sourceNodes[0].NodeType, out var sourceType))
        {
            return null;
        }

        var sourceConnectionIdRaw = ReadStringConfiguration(sourceNodes[0], "sourceConnectionId");
        if (!Guid.TryParse(sourceConnectionIdRaw, out var sourceConnectionId))
        {
            return null;
        }

        var client = _sourceClientFactory.Create(sourceType);
        var resolver = _sourceConnectionResolver;

        return async (resourceType, id, ct) =>
        {
            var source = await resolver.ResolveAsync(sourceConnectionId, null, null, ct);
            if (source is null)
            {
                return null;
            }

            var envelope = await client.ReadByIdAsync(resourceType, id, source, ct);
            return envelope?.RawJson;
        };
    }

    public override async Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        var records = PassThroughNodeExecutor.ReadMappedRecords(inputs).ToArray();
        if (records.Length == 0 && _destinationType == DestinationType.FhirRepository)
        {
            // FhirRepositoryDestination is exempt from the graph's upstream-Mapping-node requirement (see
            // WorkflowGraphValidator.DestinationRequiresMappedRecords), so when it's wired directly to a
            // source/transform node instead of a Mapping node, its input arrives as a raw ResourceBatch
            // rather than a MappedRecordBatch. Convert each resource envelope straight into a passthrough
            // record — the same shape the Part-1 MappingNodeExecutor passthrough branch already produces
            // for the (now superseded) synthetic-node case.
            records = PassThroughNodeExecutor.ReadResourceEnvelopes(inputs)
                .Select(resource => new MappedDestinationRecord(
                    context.WorkflowRunId,
                    resource.ResourceType,
                    resource.ResourceType,
                    resource.ResourceId,
                    new Dictionary<string, object?>(),
                    Convert.ToString(resource.Payload) ?? "{}"))
                .ToArray();
        }

        var destination = ReadConfiguration<DestinationConfiguration>(node, "destination")
            ?? CreateDestinationConfiguration(context, node);

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
        var fetchMissingReferenceAsync = await ResolveFetchMissingReferenceDelegateAsync(node, cancellationToken);
        var writeContext = new PipelineWriteContext(
            AllowInlineDelivery: false,
            workflowName,
            DateTimeOffset.UtcNow,
            CorrelationId: context.CorrelationId,
            FetchMissingReferenceAsync: fetchMissingReferenceAsync);

        int written;
        string? downloadUrl;
        GeneratedFile? inlineDownload;
        FHIRBridge.Application.Abstractions.Destinations.DestinationWriteResult? writeResult = null;
        var explicitProfile = ReadConfiguration<MappingProfile>(node, "mappingProfile");
        // A writer isolates per-record failures (constraint violations, conversion errors, ...) into
        // DestinationWriteResult.RecordErrors instead of throwing, so one bad record never discards the rest of
        // an otherwise-good batch — see MappedSqlServerDestinationWriter.WriteAsync. Previously nothing here ever
        // read RecordErrors, so a resource type whose every record failed to write (e.g. a NOT NULL column with
        // no mapped field) still reported node/run Status "Succeeded" with zero indication anything went wrong.
        // Collecting them into the same "skippedResourceTypes" metadata key SourceNodeExecutors uses for scope-
        // authorization skips reuses RankedWorkflowOrchestrator's existing WorkflowRunStatus.PartialSuccess
        // aggregation and its ExpectedFailure/"PartialSuccess" audit-log capture, rather than inventing a second
        // parallel reporting path.
        var writeFailureReasons = new List<string>();

        if (explicitProfile is null && MultiTableRelationalDestinationTypes.Contains(_destinationType))
        {
            // A relational destination handling more than one resource type normally sends each to its own table
            // (Patient -> dbo.Patient_New, Condition -> dbo.Condition, ...) with its own columns/upsert key. Writing
            // the whole mixed batch through one MappingProfile forced every resource type through whichever one's
            // shape happened to be saved first on this node, routing every other resource type at the wrong
            // table/columns and failing with "Invalid column name". Write each resource type's records against its
            // own resolved profile instead — ordered so a group another group's records reference via FK (see
            // OrderGroupsByReferenceDependency) is written first, and with the destination's own write-mode
            // (Upsert/Update/etc.) preserved even though a real resolved MappingProfile carries no mode suffix.
            var profilesByResourceType = CreateMappingProfiles(node, records);
            var writeModeSuffix = BuildWriteModeSuffix(node);
            var totalWritten = 0;
            string? firstDownloadUrl = null;
            var groups = records.GroupBy(record => record.ResourceType, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var group in OrderGroupsByReferenceDependency(groups))
            {
                var groupRecords = group.ToArray();
                var profile = profilesByResourceType.TryGetValue(group.Key, out var matched)
                    ? matched
                    : await ResolveMappingProfileAsync(node, group.Key, groupRecords, cancellationToken);
                // The ";mode=..." suffix convention only means anything to MappedSqlServerDestinationWriter's
                // ParseDestinationTarget — Blob reads its write mode straight off the destination's own
                // ConnectionMetadataJson (BlobDestinationSettings.Parse) regardless of grouping, so splicing this
                // onto its DestinationObject would just corrupt the blob name/folder for no benefit.
                var effectiveProfile = _destinationType == DestinationType.BlobStorage
                    ? profile
                    : ApplyWriteModeSuffix(profile, writeModeSuffix);
                var groupResult = await writer.WriteAsync(destination, effectiveProfile, groupRecords, writeContext, cancellationToken);
                totalWritten += groupResult.Count;
                firstDownloadUrl ??= groupResult.DownloadUrl;
                writeResult = groupResult;

                if (groupResult.RecordErrors is { Count: > 0 } groupErrors)
                {
                    writeFailureReasons.Add(DescribeWriteFailures(group.Key, groupRecords.Length, groupErrors));
                }
            }

            written = totalWritten;
            downloadUrl = firstDownloadUrl;
            // Inline (in-response) bytes are never produced for a multi-table write — AllowInlineDelivery is always
            // false on this engine (see the comment above writeContext), so there is nothing to attribute here.
            inlineDownload = null;
        }
        else
        {
            var mappingProfile = explicitProfile ?? CreateMappingProfile(node, preferredResourceType: null, records);
            writeResult = await writer.WriteAsync(destination, mappingProfile, records, writeContext, cancellationToken);
            written = writeResult.Count;
            downloadUrl = writeResult.DownloadUrl;
            inlineDownload = writeResult.InlineDownload;

            if (writeResult.RecordErrors is { Count: > 0 } singleProfileErrors)
            {
                writeFailureReasons.Add(DescribeWriteFailures(mappingProfile.ResourceType, records.Length, singleProfileErrors));
            }
        }

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
                    inlineDownload?.Content.Length,
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
                ["downloadUrl"] = downloadUrl,
                // Same metadata key/shape SourceNodeExecutors uses for scope-authorization skips — see the
                // writeFailureReasons doc comment above for why this destination-write case reuses it.
                ["skippedResourceTypes"] = writeFailureReasons.Count > 0 ? writeFailureReasons.ToArray() : null
            });
    }

    /// <summary>
    /// One human-readable line per resource type whose destination write left records unwritten — e.g. a NOT
    /// NULL column with no mapped field, or any other per-record constraint/conversion failure a writer isolates
    /// via <see cref="FHIRBridge.Application.Abstractions.Destinations.DestinationWriteResult.RecordErrors"/>
    /// instead of throwing. Deduplicates the underlying messages (a batch of failures is usually the same root
    /// cause repeated once per record) and caps the sample so one resource type's summary can't dwarf the rest.
    /// </summary>
    private static string DescribeWriteFailures(string resourceType, int totalCount, IReadOnlyList<string> errors)
    {
        var sample = string.Join(" | ", errors.Distinct(StringComparer.Ordinal).Take(2));
        return $"{resourceType}: {errors.Count} of {totalCount} record(s) failed to write to the destination ({sample})";
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
        if (string.IsNullOrWhiteSpace(target) && node.NodeType == WorkflowNodeTypes.FhirRepositoryDestination)
        {
            // The destination wizard only stamps a top-level "target" field onto the node when reusing an
            // EXISTING destination connection (destination-wizard.component.ts, selectExisting() branch) — a
            // freshly-created FHIR destination never gets one, even though its persisted DestinationConfigurations
            // row does (via WorkflowBuildAssemblerService.buildDestination()). Fall back to dest_baseUrl, the same
            // raw field BuildConnectionMetadataJson below already reads, so a graph-driven run of a freshly-created
            // FHIR destination doesn't throw "Target must be set" from MappedFhirRepositoryDestinationWriter.
            target = ReadStringConfiguration(node, "dest_baseUrl");
        }

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

        // The canvas node's own field is dest_authType, in the wizard's internal vocabulary ('oauth2'/'basic'/
        // 'bearer') — it's never renamed to the backend's dest_fhirAuthType/'clientCredentials' vocabulary at this
        // layer; that bridge only happens in WorkflowBuildAssemblerService.buildConnectionMetadata(), which builds
        // the SEPARATE, persisted DestinationConfiguration row. A graph-driven run reconstructs its own
        // DestinationConfiguration straight from these raw node fields (see CreateDestinationConfiguration above)
        // and never reads that persisted row, so FhirRepositoryAuthResolver would never see dest_fhirAuthType at
        // all — silently resolving to "none" and sending an unauthenticated request that Aidbox rejects with 401.
        // Mirror the same key+value bridge here, scoped to this node type only.
        if (node.NodeType == WorkflowNodeTypes.FhirRepositoryDestination
            && metadata.TryGetValue("dest_authType", out var fhirAuthType)
            && fhirAuthType.ValueKind == JsonValueKind.String)
        {
            var bridgedValue = fhirAuthType.GetString() == "oauth2" ? "clientCredentials" : fhirAuthType.GetString();
            metadata["dest_fhirAuthType"] = JsonSerializer.SerializeToElement(bridgedValue);
        }

        return metadata.Count == 0 ? null : JsonSerializer.Serialize(metadata, JsonOptions);
    }

    /// <summary>
    /// Resolves the real MappingProfile for one resource-type group of records within this destination write,
    /// when <see cref="CreateMappingProfiles"/>'s node-embedded <c>resourceMappings</c>/legacy config had nothing
    /// for this resource type. Preferring (in order): <c>mappingProfileIds</c> — the id THIS node itself saved
    /// for this resource type (see WorkflowEndpoints.cs's Mappings step) — resolving by it can never pick up a
    /// different workflow's profile; and finally the legacy synthetic profile built straight from whatever
    /// "fields" happen to be embedded on the node (kept for graphs/tests with neither of the above).
    /// Deliberately does NOT search MappingProfile by the natural key (ResourceType, SourceConnectionId,
    /// DestinationId), and does NOT fall back to "whichever profile for this DestinationId+ResourceType was
    /// modified most recently" — that triple/pair is shared by any workflow built on the same source connection
    /// + destination + resource type, so either search would silently resolve to (and, once profiles diverge,
    /// keep flapping onto) a DIFFERENT workflow's profile — the exact "Invalid column name" incident this
    /// replaces.
    /// </summary>
    private async Task<MappingProfile> ResolveMappingProfileAsync(
        WorkflowNode node,
        string resourceType,
        IReadOnlyCollection<MappedDestinationRecord> groupRecords,
        CancellationToken cancellationToken)
    {
        if (_configurationRepository is not null && ReadProfileIds(node).TryGetValue(resourceType, out var profileId))
        {
            var profile = await _configurationRepository.GetMappingProfileAsync(profileId, cancellationToken);
            if (profile is not null)
            {
                return profile;
            }
        }

        return CreateMappingProfile(node, resourceType, groupRecords);
    }

    /// <summary>Reads the destination-wide Write mode (Upsert/Update/Insert/CDC — same for every resource type
    /// this destination writes) straight off the node's own "dest_writeMode" config, as a ";mode=X" suffix ready
    /// to splice onto a resolved profile's plain DestinationObject. Null when no write mode is configured (the
    /// writer's own "Insert" default then applies, exactly as if this suffix were never spliced on).</summary>
    private static string? BuildWriteModeSuffix(WorkflowNode node)
    {
        var writeMode = ReadStringConfiguration(node, "dest_writeMode");
        return string.IsNullOrWhiteSpace(writeMode) ? null : $"mode={writeMode}";
    }

    /// <summary>Splices the destination's write-mode suffix onto a profile's DestinationObject so
    /// MappedSqlServerDestinationWriter's <c>ParseDestinationTarget</c> picks it up — needed because a REAL
    /// MappingProfile (resolved per resource-type group) carries a plain table name with no mode information at
    /// all, unlike the legacy synthetic profile this destination node used to always get. Skipped when the
    /// profile already carries a ';' (the legacy synthetic-profile path already has its own suffix baked in —
    /// splicing another on would duplicate rather than override it).</summary>
    private static MappingProfile ApplyWriteModeSuffix(MappingProfile profile, string? suffix)
    {
        if (suffix is null || profile.DestinationObject.Contains(';'))
        {
            return profile;
        }

        return new MappingProfile(
            profile.Name, profile.ResourceType, profile.SourceConnectionId, profile.DestinationId,
            $"{profile.DestinationObject};{suffix}", profile.Fields, mappingJson: profile.MappingJson);
    }

    /// <summary>
    /// Orders resource-type groups so a group referenced by another (via <see cref="MappedReferenceLookup"/>,
    /// e.g. Observation's PatientId lookup pointing at "Patient") is written first — the referenced table's rows
    /// must already exist for <see cref="MappedSqlServerDestinationWriter"/>'s lookup to find them. A simple
    /// topological sort (Kahn/DFS style); any cycle (which shouldn't occur for real FHIR reference graphs) just
    /// falls back to the original grouping order for whichever groups are involved in it, rather than looping.
    /// </summary>
    private static List<IGrouping<string, MappedDestinationRecord>> OrderGroupsByReferenceDependency(
        List<IGrouping<string, MappedDestinationRecord>> groups)
    {
        // Keyed on the bare table name (schema prefix and any ";mode=..." write-mode suffix stripped). A
        // group's own DestinationObject carries both — it's the full profile-level value (e.g.
        // "dbo.Encounter;mode=upsert") — while a reference lookup's LookupTable is just the bare name a mapped
        // field's own row targets (e.g. "Encounter", matching DestMappingRow.target's convention). Comparing the
        // two as-is never matches, which silently disabled this entire topological sort for any real (non-
        // legacy) MappingProfile — a resource referencing another via ReferenceLookupTable/ReferenceLookupKeyColumn
        // could land in either write order, failing "no row in [table] has [column] = ..." whenever the
        // referenced resource's own group happened to be written second.
        var tableToGroup = groups
            .Select(g => (Table: g.Select(r => r.DestinationObject).FirstOrDefault(), Group: g))
            .Where(x => x.Table is not null)
            .GroupBy(x => NormalizeTableName(x.Table!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Group, StringComparer.OrdinalIgnoreCase);

        var dependencies = groups.ToDictionary(
            g => g,
            g => g
                .SelectMany(r => r.ReferenceLookups ?? [])
                .Select(l => NormalizeTableName(l.LookupTable))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(table => tableToGroup.ContainsKey(table) && !ReferenceEquals(tableToGroup[table], g))
                .Select(table => tableToGroup[table])
                .ToList());

        var ordered = new List<IGrouping<string, MappedDestinationRecord>>();
        var visited = new HashSet<IGrouping<string, MappedDestinationRecord>>();
        var visiting = new HashSet<IGrouping<string, MappedDestinationRecord>>();

        void Visit(IGrouping<string, MappedDestinationRecord> group)
        {
            if (visited.Contains(group) || !visiting.Add(group))
            {
                return; // already ordered, or a cycle — stop recursing rather than looping forever
            }

            foreach (var dependency in dependencies[group])
            {
                Visit(dependency);
            }

            visiting.Remove(group);
            visited.Add(group);
            ordered.Add(group);
        }

        foreach (var group in groups)
        {
            Visit(group);
        }

        return ordered;
    }

    /// <summary>Strips a ";mode=..." write-mode suffix and any schema prefix, leaving just the bare table
    /// name — the one form both a group's own (profile-level, schema+suffix-qualified) DestinationObject and a
    /// reference lookup's (bare, per-field) LookupTable can be compared against.</summary>
    private static string NormalizeTableName(string table)
    {
        var semicolon = table.IndexOf(';');
        var withoutOptions = semicolon >= 0 ? table[..semicolon] : table;
        var dot = withoutOptions.LastIndexOf('.');
        return dot >= 0 ? withoutOptions[(dot + 1)..] : withoutOptions;
    }

    /// <summary>
    /// Builds one <see cref="MappingProfile"/> per resource type this destination node knows about — from the
    /// per-resource <c>resourceMappings</c> config (see <see cref="DestinationResourceMappingConfig"/>) plus, for
    /// backward compatibility, whichever resource type the legacy single resourceType/destinationObject/fields
    /// trio describes (a destination saved before resourceMappings existed, or never re-saved since). Used by
    /// <see cref="ExecuteAsync"/> to route each resource type in a mixed batch to its own table/columns instead of
    /// forcing every resource type through one profile.
    /// </summary>
    private static IReadOnlyDictionary<string, MappingProfile> CreateMappingProfiles(
        WorkflowNode node,
        IReadOnlyCollection<MappedDestinationRecord> records)
    {
        var profiles = new Dictionary<string, MappingProfile>(StringComparer.OrdinalIgnoreCase);

        var perResourceConfig = ReadConfiguration<Dictionary<string, DestinationResourceMappingConfig>>(node, "resourceMappings");
        if (perResourceConfig is not null)
        {
            foreach (var (resourceType, config) in perResourceConfig)
            {
                var fields = config.Fields.Select(ConfigurationMapper.ToDomain).ToList();
                profiles[resourceType] = new MappingProfile(
                    node.DisplayName, resourceType, Guid.Empty, Guid.Empty, config.DestinationObject, fields);
            }
        }

        var legacyResourceType = ReadStringConfiguration(node, "resourceType");
        if (legacyResourceType is not null && !profiles.ContainsKey(legacyResourceType))
        {
            profiles[legacyResourceType] = CreateMappingProfile(node, legacyResourceType, records);
        }

        return profiles;
    }

    private static MappingProfile CreateMappingProfile(
        WorkflowNode node,
        string? preferredResourceType,
        IReadOnlyCollection<MappedDestinationRecord> records)
    {
        var staticResourceType = ReadStringConfiguration(node, "resourceType");
        var resourceType = preferredResourceType
            ?? staticResourceType
            ?? records.FirstOrDefault()?.ResourceType
            ?? "Patient";

        // The node's static destinationObject/fields only actually describe `resourceType` when nothing more
        // specific was requested, or when the caller explicitly asked for the same resource type the static shape
        // covers. Applying Patient's static shape to a Condition (or Observation) fallback would silently
        // reintroduce the very "Invalid column name" bug this method exists to avoid — fall back to what the
        // record itself carries instead, since MappingNodeExecutor already stamps each record with its own
        // correct DestinationObject.
        var staticShapeAppliesToThisType = staticResourceType is null
            || string.Equals(staticResourceType, resourceType, StringComparison.OrdinalIgnoreCase);

        var destinationObject = (staticShapeAppliesToThisType ? ReadStringConfiguration(node, "destinationObject") : null)
            ?? records.FirstOrDefault()?.DestinationObject
            ?? resourceType;

        // The writer creates/aligns the target table's columns from these fields, so carry them from node config
        // (the projection embeds the same fields the mapping node used) rather than defaulting to none.
        var fields = ((staticShapeAppliesToThisType
                ? ReadConfiguration<IReadOnlyCollection<MappingFieldDto>>(node, "fields")
                : null) ?? [])
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
