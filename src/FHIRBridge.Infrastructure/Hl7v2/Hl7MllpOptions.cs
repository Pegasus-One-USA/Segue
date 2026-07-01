namespace FHIRBridge.Infrastructure.Hl7v2;

/// <summary>Configuration for the HL7 v2 MLLP listener (Signal layer legacy message ingestion).</summary>
public sealed class Hl7MllpOptions
{
    /// <summary>When false (default), the MLLP listener does not start.</summary>
    public bool Enabled { get; set; }

    /// <summary>TCP port the MLLP listener binds to. 2575 is the IANA-registered HL7 port.</summary>
    public int Port { get; set; } = 2575;

    /// <summary>Tenant that ingested HL7 v2 messages are attributed to.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Webhook configuration that maps the ingested resources to a pipeline.</summary>
    public Guid WebhookConfigurationId { get; set; }
}
