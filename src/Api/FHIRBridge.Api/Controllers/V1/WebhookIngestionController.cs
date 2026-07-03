using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Abstractions.Pipeline;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[AllowAnonymous]
[Route("api/v1/webhooks/{webhookConfigurationId:guid}/ingest")]
public sealed class WebhookIngestionController : ControllerBase
{
    private readonly IConfiguredPipelineService _configuredPipelineService;
    private readonly IWebhookIngestionDispatcher _webhookDispatcher;
    private readonly bool _async;

    public WebhookIngestionController(
        IConfiguredPipelineService configuredPipelineService,
        IWebhookIngestionDispatcher webhookDispatcher,
        IConfiguration configuration)
    {
        _configuredPipelineService = configuredPipelineService;
        _webhookDispatcher = webhookDispatcher;
        _async = bool.TryParse(configuration["WebhookIngestion:Async"], out var enabled) && enabled;
    }

    [HttpPost]
    [ProducesResponseType(typeof(ConfiguredPipelineRunDto), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Ingest(
        Guid webhookConfigurationId,
        [FromBody] JsonElement payload,
        CancellationToken cancellationToken)
    {
        var request = CreateRequest(payload);

        // Async mode: acknowledge fast and process off the request thread (requires a shared transport — see Phase 3).
        if (_async)
        {
            var payloadHash = ComputePayloadHash(request.ResourceJson);
            var messageId = $"webhook:{webhookConfigurationId:N}:{payloadHash}";
            await _webhookDispatcher.EnqueueAsync(
                new WebhookIngestionCommand(
                    webhookConfigurationId,
                    request.ResourceJson,
                    payloadHash,
                    request.TriggeredBy,
                    request.CorrelationId,
                    messageId),
                cancellationToken);

            return Accepted(new { status = "queued", messageId });
        }

        // Synchronous mode (default): run inline and return the resulting run.
        var pipelineRun = await _configuredPipelineService.StartWebhookAsync(
            webhookConfigurationId,
            request,
            cancellationToken);

        return Accepted($"/api/v1/pipeline-runs/{pipelineRun.Id}", pipelineRun);
    }

    private static WebhookIngestionRequest CreateRequest(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("resourceJson", out var resourceJsonProperty) &&
            resourceJsonProperty.ValueKind == JsonValueKind.String)
        {
            return new WebhookIngestionRequest(
                resourceJsonProperty.GetString() ?? string.Empty,
                GetString(payload, "triggeredBy"),
                GetString(payload, "correlationId"));
        }

        return new WebhookIngestionRequest(
            payload.GetRawText(),
            "webhook",
            null);
    }

    private static string ComputePayloadHash(string resourceJson)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(resourceJson));

        return Convert.ToHexString(hash);
    }

    private static string? GetString(JsonElement payload, string propertyName)
    {
        return payload.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }
}
