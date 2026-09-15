namespace FHIRBridge.Application.Services.Workflows.Numbering;

/// <summary>
/// When the incremental segment of a generated workflow number (see <see cref="WorkflowNumberPeriod"/>)
/// restarts at 1.
/// </summary>
/// <remarks>
/// Each value maps to a "period key" string rather than to a scheduled reset job — a new period is simply a
/// key no counter row exists for yet, so it starts at 1 on its own. Nothing has to run at midnight, and there
/// is no reset that can fail to fire or fire twice.
/// </remarks>
public enum WorkflowNumberResetPolicy
{
    /// <summary>One continuous sequence forever; the date segment still changes, the counter never restarts.</summary>
    Never = 0,

    Daily = 1,

    Monthly = 2,

    /// <summary>Calendar quarters — Jan-Mar, Apr-Jun, Jul-Sep, Oct-Dec.</summary>
    Quarterly = 3,

    /// <summary>Calendar years, restarting on 1 January.</summary>
    Yearly = 4,

    /// <summary>Yearly, but restarting on an admin-chosen anniversary (WorkflowNumbering:AnchorDate, "MM-dd")
    /// rather than 1 January — a fiscal year, typically. A date before this year's anniversary belongs to the
    /// cycle that opened on last year's.</summary>
    CustomAnchorDate = 5,
}
