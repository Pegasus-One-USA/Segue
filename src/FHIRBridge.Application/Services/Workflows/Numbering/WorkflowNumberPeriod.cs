using System.Globalization;

namespace FHIRBridge.Application.Services.Workflows.Numbering;

/// <summary>
/// Turns "when should the counter reset?" into "what string identifies the current period?", which is the whole
/// of the reset feature: the counter table is keyed by this string, so a new period has no row yet and therefore
/// starts at 1 by itself. No scheduled reset job exists, or is needed.
/// </summary>
public static class WorkflowNumberPeriod
{
    /// <summary>Default anniversary for <see cref="WorkflowNumberResetPolicy.CustomAnchorDate"/> when the admin
    /// has not set one — 1 April, the most common fiscal-year start among the policies this supports.</summary>
    public const string DefaultAnchorDate = "04-01";

    /// <summary>
    /// The period key for <paramref name="utcNow"/> under <paramref name="policy"/>. Keys are prefixed by policy
    /// so that changing the policy can never land on a key a different policy already used (e.g. "2026" as both a
    /// yearly key and a custom-anchor key), which would otherwise resume someone else's sequence mid-way.
    /// </summary>
    /// <param name="anchorDate">"MM-dd", only read for <see cref="WorkflowNumberResetPolicy.CustomAnchorDate"/>.
    /// Unparseable or absent input falls back to <see cref="DefaultAnchorDate"/> rather than throwing — a bad
    /// value in a settings row must not block workflow creation outright.</param>
    public static string KeyFor(WorkflowNumberResetPolicy policy, DateTime utcNow, string? anchorDate = null)
    {
        return policy switch
        {
            WorkflowNumberResetPolicy.Never => "never",
            WorkflowNumberResetPolicy.Daily => $"daily:{utcNow:yyyy-MM-dd}",
            WorkflowNumberResetPolicy.Monthly => $"monthly:{utcNow:yyyy-MM}",
            WorkflowNumberResetPolicy.Quarterly => $"quarterly:{utcNow:yyyy}-Q{((utcNow.Month - 1) / 3) + 1}",
            WorkflowNumberResetPolicy.Yearly => $"yearly:{utcNow:yyyy}",
            WorkflowNumberResetPolicy.CustomAnchorDate => $"anchor:{AnchorCycleStart(utcNow, anchorDate):yyyy-MM-dd}",
            _ => $"daily:{utcNow:yyyy-MM-dd}",
        };
    }

    /// <summary>
    /// The date the current anchored cycle opened. A moment before this year's anniversary belongs to the cycle
    /// that opened on LAST year's, so the key stays stable across the calendar-year boundary for any anchor other
    /// than 1 January.
    /// </summary>
    internal static DateTime AnchorCycleStart(DateTime utcNow, string? anchorDate)
    {
        var (month, day) = ParseAnchor(anchorDate);

        // Clamp to the month's real length so a 02-29 anchor still resolves in a non-leap year (to 02-28)
        // instead of throwing on construction.
        var thisYear = new DateTime(utcNow.Year, month, Math.Min(day, DateTime.DaysInMonth(utcNow.Year, month)));
        if (utcNow.Date >= thisYear.Date)
        {
            return thisYear;
        }

        var priorYear = utcNow.Year - 1;
        return new DateTime(priorYear, month, Math.Min(day, DateTime.DaysInMonth(priorYear, month)));
    }

    private static (int Month, int Day) ParseAnchor(string? anchorDate)
    {
        var candidate = string.IsNullOrWhiteSpace(anchorDate) ? DefaultAnchorDate : anchorDate.Trim();

        // "MM-dd" is the stored shape, but accept "M-d" too rather than rejecting an admin's hand-typed "4-1".
        var parts = candidate.Split('-');
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var month)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var day)
            && month is >= 1 and <= 12
            && day is >= 1 and <= 31)
        {
            return (month, day);
        }

        return (4, 1);
    }
}
