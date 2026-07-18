namespace FHIRBridge.Infrastructure.Messaging.RabbitMq;

/// <summary>Connection and queue settings for the RabbitMQ messaging provider (bound from <c>Messaging:RabbitMq</c>).</summary>
public sealed class RabbitMqOptions
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    /// <summary>HTTP management API port (rabbitmq:3-management image) — used only for Queue Monitor, never for AMQP.</summary>
    public int ManagementPort { get; set; } = 15672;
    public string UserName { get; set; } = "fhirbridge";
    public string Password { get; set; } = "fhirbridge";
    public string VirtualHost { get; set; } = "/";
    public string PipelineRunsQueue { get; set; } = "pipeline-runs";
    public string WebhookIngestionQueue { get; set; } = "webhook-ingestion";
    public ushort PrefetchCount { get; set; } = 10;
}
