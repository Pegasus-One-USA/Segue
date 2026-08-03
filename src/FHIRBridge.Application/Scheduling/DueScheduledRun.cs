namespace FHIRBridge.Application.Scheduling;

/// <summary>
/// A claimed batch of scheduled-pull routes that are due, with the distinct resource types to run and the
/// specific route ids that were claimed (so the processor runs exactly those routes).
/// </summary>
public sealed record DueScheduledRun(
    IReadOnlyList<string> ResourceTypes,
    IReadOnlyList<Guid> RouteIds)
{
    /// <summary>Human-readable label per claimed route (its mapping profile's name, falling back to its
    /// resource type, falling back to the route id) — for display in SchedulerHistory instead of a raw
    /// GUID list. Same order as <see cref="RouteIds"/>.</summary>
    public IReadOnlyList<string> RouteLabels { get; init; } = [];
}
