using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.Fhir;

namespace FHIRBridge.Worker;

public sealed class RuntimeWorkerOptions
{
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Direct-call route scheduling (Worker.RunDueRoutesAsync) — disabled by default now that
    /// ScheduleDispatcherWorker/PipelineRunCommandProcessor (the queue-based path) is the live scheduler. Kept as a
    /// fast-rollback switch: set true to fall back to direct polling without a redeploy if the queue path misbehaves.
    /// Workflow scheduling (RunDueWorkflowsAsync) has no queue-based equivalent and always runs regardless of this flag.
    /// </summary>
    public bool DirectRouteSchedulingEnabled { get; set; }

    public string[] ResourceTypes { get; set; } = SupportedFhirResourceTypes.All.ToArray();
    public RuntimeWorkerSourceOptions Source { get; set; } = new();
    public RuntimeWorkerDestinationOptions Destination { get; set; } = new();
}

public sealed class RuntimeWorkerSourceOptions
{
    public RuntimeSourceType SourceType { get; set; } = RuntimeSourceType.Sample;
    public string? Name { get; set; } = "Sample Phase 1 Source";
    public string? BaseUrl { get; set; }
    public string? TokenEndpoint { get; set; }
    public string? ClientId { get; set; }
    public string? KeyId { get; set; }
    public string? PrivateKeyPem { get; set; }
    public string[] Scopes { get; set; } = ["system/*.read"];
    public int SearchCount { get; set; } = 100;
    public int MaxPages { get; set; } = 1;
}

public sealed class RuntimeWorkerDestinationOptions
{
    public RuntimeDestinationType DestinationType { get; set; } = RuntimeDestinationType.InMemory;
    public string? ConnectionString { get; set; }
    public string? SchemaName { get; set; } = "fhirbridge";
}
