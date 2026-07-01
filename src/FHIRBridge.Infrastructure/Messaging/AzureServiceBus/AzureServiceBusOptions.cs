namespace FHIRBridge.Infrastructure.Messaging.AzureServiceBus;

/// <summary>
/// Azure Service Bus settings (bound from <c>Messaging:AzureServiceBus</c>). Provide either a connection string
/// (dev) or a fully-qualified namespace for managed-identity auth via DefaultAzureCredential (prod). Queues are
/// provisioned by the marketplace Bicep templates, not created at runtime.
/// </summary>
public sealed class AzureServiceBusOptions
{
    public string? ConnectionString { get; set; }
    public string? FullyQualifiedNamespace { get; set; }
    public string PipelineRunsQueue { get; set; } = "pipeline-runs";
    public string WebhookIngestionQueue { get; set; } = "webhook-ingestion";
    public int MaxConcurrentCalls { get; set; } = 5;
}
