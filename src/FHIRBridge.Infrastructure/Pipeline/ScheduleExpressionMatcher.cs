namespace FHIRBridge.Infrastructure.Pipeline;

public static class ScheduleExpressionMatcher
{
    /// <summary>
    /// Maximum window (in minutes) the catch-up scan looks back over. Bounds work when a route was disabled or the
    /// dispatcher was down for a long time — at most one run is produced per re-enabled route, not one per missed slot.
    /// </summary>
    private const int MaxCatchUpMinutes = 1440;

    /// <summary>
    /// Returns true when the schedule has at least one matching minute in the window
    /// (<paramref name="lastTriggeredOnUtc"/>, <paramref name="utcNow"/>]. This makes scheduling catch-up aware:
    /// a slot missed while the dispatcher was down still fires once on the next evaluation. When the route has never
    /// been triggered, only the current minute is considered (no historical backfill on first enable).
    /// </summary>
    public static bool IsDueSince(string? scheduleExpression, DateTime? lastTriggeredOnUtc, DateTime utcNow)
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
        else
        {
            start = nowMinute;
        }

        for (var slot = start; slot <= nowMinute; slot = slot.AddMinutes(1))
        {
            if (IsDue(scheduleExpression, slot))
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
    public static DateTime? NextDueAfter(string? scheduleExpression, DateTime fromUtc, int maxMinutesToScan = 10080)
    {
        if (string.IsNullOrWhiteSpace(scheduleExpression))
        {
            return null;
        }

        var slot = TruncateToMinuteUtc(fromUtc).AddMinutes(1);
        for (var i = 0; i < maxMinutesToScan; i++, slot = slot.AddMinutes(1))
        {
            if (IsDue(scheduleExpression, slot))
            {
                return slot;
            }
        }

        return null;
    }

    public static bool IsDue(string? scheduleExpression, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(scheduleExpression))
        {
            return false;
        }

        var normalizedUtc = utcNow.Kind == DateTimeKind.Utc
            ? utcNow
            : utcNow.ToUniversalTime();

        return scheduleExpression
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(expression => IsCronDue(expression, normalizedUtc));
    }

    private static bool IsCronDue(string expression, DateTime utcNow)
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

        return MatchesField(parts[1], utcNow.Minute, 0, 59) &&
               MatchesField(parts[2], utcNow.Hour, 0, 23) &&
               MatchesField(parts[3], utcNow.Day, 1, 31) &&
               MatchesField(parts[4], utcNow.Month, 1, 12) &&
               MatchesField(parts[5], (int)utcNow.DayOfWeek, 0, 7, allowSevenAsSunday: true);
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
