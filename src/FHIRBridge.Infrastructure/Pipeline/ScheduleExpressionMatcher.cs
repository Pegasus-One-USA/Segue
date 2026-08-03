using System.Collections.Concurrent;

namespace FHIRBridge.Infrastructure.Pipeline;

public static class ScheduleExpressionMatcher
{
    /// <summary>
    /// Maximum window (in minutes) the catch-up scan looks back over. Bounds work when a route was disabled or the
    /// dispatcher was down for a long time — at most one run is produced per re-enabled route, not one per missed slot.
    /// </summary>
    private const int MaxCatchUpMinutes = 1440;

    private static readonly ConcurrentDictionary<string, TimeZoneInfo> TimeZoneCache = new();

    /// <summary>
    /// Resolves an IANA/Windows time zone id to a <see cref="TimeZoneInfo"/>, caching lookups since
    /// <see cref="TimeZoneInfo.FindSystemTimeZoneById"/> is called on every dispatcher tick. Falls back to UTC
    /// (and never throws) for a null/blank/unrecognized id, so a bad value can't silently break scheduling.
    /// </summary>
    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId) || timeZoneId == "UTC")
        {
            return TimeZoneInfo.Utc;
        }

        return TimeZoneCache.GetOrAdd(timeZoneId, static id =>
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                return TimeZoneInfo.Utc;
            }
            catch (InvalidTimeZoneException)
            {
                return TimeZoneInfo.Utc;
            }
        });
    }

    /// <summary>
    /// Returns true when the schedule has at least one matching minute in the window
    /// (<paramref name="lastTriggeredOnUtc"/>, <paramref name="utcNow"/>]. This makes scheduling catch-up aware:
    /// a slot missed while the dispatcher was down (or simply between poll ticks) still fires once on the next
    /// evaluation. When the route/workflow has never been triggered, the window starts at
    /// <paramref name="createdOnUtc"/> instead (still bounded by <see cref="MaxCatchUpMinutes"/>), so a slot missed
    /// between creation and the first poll after it still fires — without replaying an unbounded backlog if
    /// <paramref name="createdOnUtc"/> is old and the schedule was only just enabled.
    /// <paramref name="timeZoneId"/> is the zone the cron fields are evaluated in — defaults to UTC.
    /// </summary>
    public static bool IsDueSince(
        string? scheduleExpression,
        DateTime? lastTriggeredOnUtc,
        DateTime utcNow,
        string? timeZoneId = "UTC",
        DateTime? createdOnUtc = null)
    {
        if (string.IsNullOrWhiteSpace(scheduleExpression))
        {
            return false;
        }

        var nowMinute = TruncateToMinuteUtc(utcNow);
        var earliest = nowMinute.AddMinutes(-MaxCatchUpMinutes);

        DateTime start;
        if (lastTriggeredOnUtc is { } last)
        {
            var afterLast = TruncateToMinuteUtc(last).AddMinutes(1);
            start = afterLast > earliest ? afterLast : earliest;
        }
        else if (createdOnUtc is { } created)
        {
            var createdMinute = TruncateToMinuteUtc(created);
            start = createdMinute > earliest ? createdMinute : earliest;
        }
        else
        {
            start = nowMinute;
        }

        for (var slot = start; slot <= nowMinute; slot = slot.AddMinutes(1))
        {
            if (IsDue(scheduleExpression, slot, timeZoneId))
            {
                return true;
            }
        }

        return false;
    }

    private static DateTime TruncateToMinuteUtc(DateTime value)
    {
        // Both inputs are already UTC: DateTime.UtcNow, and LastTriggeredOnUtc stored as DATETIME2 (UTC) which EF
        // reads back as DateTimeKind.Unspecified. We must NOT call ToUniversalTime() here — on a non-UTC machine
        // that would treat the Unspecified value as local time and shift it by the offset, breaking catch-up.
        return new DateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, DateTimeKind.Utc);
    }

    /// <summary>
    /// Scans forward minute-by-minute from <paramref name="fromUtc"/> (exclusive) for the next matching slot —
    /// backs the Scheduler summary screen's "Next Run" column. Returns null if nothing matches within
    /// <paramref name="maxMinutesToScan"/> (default 7 days — generous headroom over any realistic cron cadence).
    /// </summary>
    public static DateTime? NextDueAfter(
        string? scheduleExpression,
        DateTime fromUtc,
        int maxMinutesToScan = 10080,
        string? timeZoneId = "UTC")
    {
        if (string.IsNullOrWhiteSpace(scheduleExpression))
        {
            return null;
        }

        var slot = TruncateToMinuteUtc(fromUtc).AddMinutes(1);
        for (var i = 0; i < maxMinutesToScan; i++, slot = slot.AddMinutes(1))
        {
            if (IsDue(scheduleExpression, slot, timeZoneId))
            {
                return slot;
            }
        }

        return null;
    }

    public static bool IsDue(string? scheduleExpression, DateTime utcNow, string? timeZoneId = "UTC")
    {
        if (string.IsNullOrWhiteSpace(scheduleExpression))
        {
            return false;
        }

        var normalizedUtc = utcNow.Kind == DateTimeKind.Utc
            ? utcNow
            : DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

        var zone = ResolveTimeZone(timeZoneId);
        var zoneLocal = TimeZoneInfo.ConvertTimeFromUtc(normalizedUtc, zone);

        return scheduleExpression
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(expression => IsCronDue(expression, zoneLocal));
    }

    /// <summary>
    /// Matches cron fields against <paramref name="zoneLocalTime"/> — the wall-clock instant already converted
    /// into the schedule's configured time zone by <see cref="IsDue"/>. Re-deriving that conversion fresh on every
    /// evaluation (rather than baking a fixed UTC offset once) is what makes DST transitions handle themselves:
    /// the same cron expression naturally shifts its effective UTC instant across a DST boundary.
    /// </summary>
    private static bool IsCronDue(string expression, DateTime zoneLocalTime)
    {
        var parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 5)
        {
            parts = ["0", parts[0], parts[1], parts[2], parts[3], parts[4]];
        }

        if (parts.Length != 6)
        {
            return false;
        }

        return MatchesField(parts[1], zoneLocalTime.Minute, 0, 59) &&
               MatchesField(parts[2], zoneLocalTime.Hour, 0, 23) &&
               MatchesField(parts[3], zoneLocalTime.Day, 1, 31) &&
               MatchesField(parts[4], zoneLocalTime.Month, 1, 12) &&
               MatchesField(parts[5], (int)zoneLocalTime.DayOfWeek, 0, 7, allowSevenAsSunday: true);
    }

    private static bool MatchesField(
        string field,
        int value,
        int min,
        int max,
        bool allowSevenAsSunday = false)
    {
        if (field is "*" or "?")
        {
            return true;
        }

        foreach (var token in field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (MatchesToken(token, value, min, max, allowSevenAsSunday))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesToken(
        string token,
        int value,
        int min,
        int max,
        bool allowSevenAsSunday)
    {
        var stepParts = token.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (stepParts.Length > 2)
        {
            return false;
        }

        var rangePart = stepParts[0];
        var step = 1;
        if (stepParts.Length == 2 &&
            (!int.TryParse(stepParts[1], out step) || step <= 0))
        {
            return false;
        }

        var start = min;
        var end = max;
        if (rangePart != "*")
        {
            var range = rangePart.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (range.Length == 1)
            {
                if (!TryParseCronValue(range[0], allowSevenAsSunday, out start))
                {
                    return false;
                }

                end = stepParts.Length == 2 ? max : start;
            }
            else if (range.Length == 2)
            {
                if (!TryParseCronValue(range[0], allowSevenAsSunday, out start) ||
                    !TryParseCronValue(range[1], allowSevenAsSunday, out end))
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        if (start < min || end > max || start > end || value < start || value > end)
        {
            return false;
        }

        return (value - start) % step == 0;
    }

    private static bool TryParseCronValue(string value, bool allowSevenAsSunday, out int result)
    {
        if (!int.TryParse(value, out result))
        {
            return false;
        }

        if (allowSevenAsSunday && result == 7)
        {
            result = 0;
        }

        return true;
    }
}
