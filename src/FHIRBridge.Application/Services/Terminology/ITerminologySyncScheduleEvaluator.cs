namespace FHIRBridge.Application.Services.Terminology;

/// <summary>
/// Single source of truth for "is a terminology code's scheduled sync due right now", shared by
/// every terminology sync worker instead of each one hand-rolling its own due-check. Safe to
/// inject into singleton hosted services — it only reads through <c>ISystemSettingsCache</c>.
/// </summary>
public interface ITerminologySyncScheduleEvaluator
{
    Task<bool> IsDueAsync(
        TerminologySyncScheduleConfig config, DateTimeOffset now, CancellationToken cancellationToken);
}
