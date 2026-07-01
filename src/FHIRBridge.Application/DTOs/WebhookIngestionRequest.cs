namespace FHIRBridge.Application.DTOs;

public sealed record WebhookIngestionRequest(
    string ResourceJson,
    string? TriggeredBy,
    string? CorrelationId);
