using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Abstractions.Pipeline;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[AllowAnonymous]
[EnableRateLimiting("webhook")]
[Route("api/v1/webhooks/{webhookConfigurationId:guid}/ingest")]
public sealed class WebhookIngestionController : ControllerBase
{
    private readonly IConfiguredPipelineService _configuredPipelineService;
    private readonly IWebhookIngestionDispatcher _webhookDispatcher;
    private readonly bool _async;
    private readonly bool _requireSignature;
    private readonly string? _signingSecret;
    private readonly string _signatureHeader;

    public WebhookIngestionController(
        IConfiguredPipelineService configuredPipelineService,
        IWebhookIngestionDispatcher webhookDispatcher,
        IConfiguration configuration)
    {
        _configuredPipelineService = configuredPipelineService;
        _webhookDispatcher = webhookDispatcher;
        _async = bool.TryParse(configuration["WebhookIngestion:Async"], out var enabled) && enabled;
        // Signature verification is ON by default (HIPAA/SOC2): unauthenticated ingestion must prove
        // it came from the trusted sender. Deployments that terminate authenticity upstream can opt out.
        _requireSignature = !bool.TryParse(configuration["WebhookIngestion:RequireSignature"], out var require) || require;
        _signingSecret = configuration["WebhookIngestion:SigningSecret"];
        _signatureHeader = configuration["WebhookIngestion:SignatureHeader"] ?? "X-FHIRBridge-Signature";
    }

    [HttpPost]
    [ProducesResponseType(typeof(ConfiguredPipelineRunDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Ingest(
        Guid webhookConfigurationId,
        CancellationToken cancellationToken)
    {
        // Read the raw body so the HMAC is computed over the exact bytes the sender signed.
        string rawBody;
        Request.EnableBuffering();
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            rawBody = await reader.ReadToEndAsync(cancellationToken);
            Request.Body.Position = 0;
        }

        if (!IsSignatureValid(rawBody))
        {
            return Unauthorized(new { error = "invalid_signature", message = "Webhook signature is missing or invalid." });
        }

        JsonElement payload;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(rawBody) ? "{}" : rawBody);
            payload = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "invalid_payload", message = "Request body is not valid JSON." });
        }

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

    /// <summary>
    /// Verifies the HMAC-SHA256 signature of the raw request body against the configured signing
    /// secret. The sender must send the header (default <c>X-FHIRBridge-Signature</c>) as either
    /// <c>sha256=&lt;hex&gt;</c> or a bare hex digest. When signature verification is disabled the
    /// request is allowed through unconditionally.
    /// </summary>
    private bool IsSignatureValid(string rawBody)
    {
        if (!_requireSignature)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(_signingSecret))
        {
            // Fail closed: signatures are required but no secret is configured to verify them.
            return false;
        }

        string? provided = Request.Headers[_signatureHeader];
        if (string.IsNullOrWhiteSpace(provided))
        {
            return false;
        }

        var prefix = "sha256=";
        if (provided.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            provided = provided[prefix.Length..];
        }

        var expected = Convert.ToHexString(
            HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(_signingSecret),
                Encoding.UTF8.GetBytes(rawBody)));

        var providedBytes = Encoding.UTF8.GetBytes(provided.Trim().ToUpperInvariant());
        var expectedBytes = Encoding.UTF8.GetBytes(expected);

        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
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
