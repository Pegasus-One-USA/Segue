using System.Globalization;
using FHIRBridge.Application.Abstractions.Caching;

namespace FHIRBridge.Application.Services.Terminology;

public sealed class TerminologySyncScheduleEvaluator : ITerminologySyncScheduleEvaluator
{
    private readonly ISystemSettingsCache _settings;

    public TerminologySyncScheduleEvaluator(ISystemSettingsCache settings) => _settings = settings;

    public async Task<bool> IsDueAsync(
        TerminologySyncScheduleConfig config, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var prefix = config.SettingsKeyPrefix;

        var enabled = await _settings.GetBoolAsync($"{prefix}:SchedulerEnabled", false, cancellationToken);
        if (!enabled)
        {
            return false;
        }

        var configuredTime = await _settings.GetStringAsync(
            $"{prefix}:ExecutionTime", config.DefaultExecutionTime, cancellationToken);
        var dueTime = TimeOnly.TryParse(configuredTime, out var time)
            ? time
            : TimeOnly.Parse(config.DefaultExecutionTime, CultureInfo.InvariantCulture);
        if (now.TimeOfDay < dueTime.ToTimeSpan())
        {
            return false;
        }

        if (!await IsCadenceDayAsync(config, now, cancellationToken))
        {
            return false;
        }

        return !await HasAlreadyRunTodayAsync(prefix, now, cancellationToken);
    }

    private async Task<bool> IsCadenceDayAsync(
        TerminologySyncScheduleConfig config, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (config.FixedCadence is { } fixedCadence)
        {
            return fixedCadence(now);
        }

        var defaultFrequency = config.DefaultFrequency ?? TerminologySyncFrequency.Monthly;
        var configuredFrequency = await _settings.GetStringAsync(
            $"{config.SettingsKeyPrefix}:Frequency", defaultFrequency.ToString(), cancellationToken);
        var frequency = Enum.TryParse<TerminologySyncFrequency>(configuredFrequency, ignoreCase: true, out var parsed)
            ? parsed
            : defaultFrequency;

        return frequency switch
        {
            TerminologySyncFrequency.Daily => true,
            TerminologySyncFrequency.Weekly => now.DayOfWeek == DayOfWeek.Monday,
            TerminologySyncFrequency.Monthly => now.Day == 1,
            _ => now.Day == 1,
        };
    }

    private async Task<bool> HasAlreadyRunTodayAsync(
        string prefix, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lastRunRaw = await _settings.GetStringAsync($"{prefix}:LastRunUtc", string.Empty, cancellationToken);
        if (!DateTime.TryParse(
                lastRunRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastRunUtc))
        {
            return false;
        }

        var lastRunLocal = DateOnly.FromDateTime(lastRunUtc.ToLocalTime());
        return lastRunLocal == DateOnly.FromDateTime(now.DateTime);
    }
}
