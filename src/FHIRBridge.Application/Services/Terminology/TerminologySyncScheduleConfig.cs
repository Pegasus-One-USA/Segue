namespace FHIRBridge.Application.Services.Terminology;

/// <summary>
/// Describes how a single terminology code's scheduled sync is configured, so
/// <see cref="ITerminologySyncScheduleEvaluator"/> can evaluate due-ness the same way for every
/// code without each sync worker hand-rolling its own due-check.
/// </summary>
/// <param name="SettingsKeyPrefix">e.g. <c>"Terminology:CvxHapi"</c> — the evaluator reads
/// <c>{prefix}:SchedulerEnabled</c>, <c>{prefix}:ExecutionTime</c>, <c>{prefix}:Frequency</c>
/// (when <paramref name="DefaultFrequency"/> is set) and <c>{prefix}:LastRunUtc</c> under it.</param>
/// <param name="DefaultExecutionTime">Fallback "HH:mm" local time when no setting row exists.</param>
/// <param name="DefaultFrequency">Fallback frequency, and signal that this code's cadence is
/// configurable via a <c>Frequency</c> setting. Leave <see langword="null"/> for a code with a
/// fixed external release cadence (RxNorm, SNOMED) — supply <paramref name="FixedCadence"/>
/// instead and no <c>Frequency</c> setting will ever be read.</param>
/// <param name="FixedCadence">When <paramref name="DefaultFrequency"/> is <see langword="null"/>,
/// this predicate decides whether today is a sync day. Ignored otherwise.</param>
public sealed record TerminologySyncScheduleConfig(
    string SettingsKeyPrefix,
    string DefaultExecutionTime,
    TerminologySyncFrequency? DefaultFrequency = TerminologySyncFrequency.Monthly,
    Func<DateTimeOffset, bool>? FixedCadence = null);
