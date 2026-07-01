namespace FHIRBridge.Application.Scheduling;

/// <summary>
/// A tenant that has at least one scheduled-pull route due, with the distinct resource types to run and the
/// specific route ids that were claimed (so the processor runs exactly those routes).
/// </summary>
public sealed record DueScheduledRun(
    Guid TenantId,
    IReadOnlyList<string> ResourceTypes,
    IReadOnlyList<Guid> RouteIds);
