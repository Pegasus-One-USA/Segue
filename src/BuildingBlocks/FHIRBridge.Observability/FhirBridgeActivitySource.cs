using System.Diagnostics;

namespace FHIRBridge.Observability;

/// <summary>
/// Custom <see cref="ActivitySource"/> for pipeline-stage spans (Worker command handlers, Runtime pipeline
/// orchestration) that otherwise have no Activity of their own — unlike API requests, which ASP.NET Core's own
/// instrumentation already wraps in an Activity. Registered via <c>.AddSource(Name)</c> in
/// <see cref="ObservabilityServiceCollectionExtensions.AddFhirBridgeObservability"/> so spans started from this
/// source are actually exported.
/// </summary>
public static class FhirBridgeActivitySource
{
    public const string Name = "FHIRBridge.Pipeline";

    public static readonly ActivitySource Instance = new(Name);
}
