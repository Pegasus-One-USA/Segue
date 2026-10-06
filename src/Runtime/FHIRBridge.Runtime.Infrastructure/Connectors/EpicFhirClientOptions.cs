namespace FHIRBridge.Runtime.Infrastructure.Connectors;

public sealed class EpicFhirClientOptions
{
    public int MaxRetryCount { get; set; } = 3;
    public int BaseRetryDelayMilliseconds { get; set; } = 500;
    public int MaxRetryDelaySeconds { get; set; } = 30;
    public int MaxRetryJitterMilliseconds { get; set; } = 250;
    public int RequestTimeoutSeconds { get; set; } = 100;
    public int MinimumMillisecondsBetweenRequestsPerSource { get; set; } = 100;
}
