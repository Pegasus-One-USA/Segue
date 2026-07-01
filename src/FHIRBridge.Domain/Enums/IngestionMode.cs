namespace FHIRBridge.Domain.Enums;

public enum IngestionMode
{
    Webhook = 1,
    ScheduledPull = 2,
    WebhookAndScheduledPull = 3
}
