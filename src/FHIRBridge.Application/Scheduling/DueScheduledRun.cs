namespace FHIRBridge.Application.Scheduling;

/// <summary>
/// A claimed batch of scheduled-pull routes that are due, with the distinct resource types to run and the
/// specific route ids that were claimed (so the processor runs exactly those routes).
/// </summary>
public sealed record DueScheduledRun(
    IReadOnlyList<string> ResourceTypes,
    IReadOnlyList<Guid> RouteIds);
