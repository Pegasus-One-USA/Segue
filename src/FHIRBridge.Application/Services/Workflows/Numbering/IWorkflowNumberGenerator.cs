namespace FHIRBridge.Application.Services.Workflows.Numbering;

/// <summary>
/// Allocates the next human-quotable workflow number (e.g. <c>WLW-150926-0042</c>).
/// </summary>
public interface IWorkflowNumberGenerator
{
    /// <summary>
    /// Consumes and returns the next number for the current period, or null when numbering is disabled
    /// (<c>WorkflowNumbering:Enabled</c> = false).
    /// </summary>
    /// <remarks>
    /// Call ONLY for a genuine new workflow. The value is consumed: calling this for an edit would burn a
    /// number and renumber a workflow users may already have quoted elsewhere. Participates in the caller's
    /// ambient transaction, so a rolled-back create does not leave the counter advanced.
    /// </remarks>
    Task<string?> NextAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Renders what <see cref="NextAsync"/> would produce right now WITHOUT consuming a number — backs the
    /// settings screen's format preview. Uses the current counter value plus one, so the preview matches the
    /// number the next created workflow actually receives.
    /// </summary>
    Task<string> PreviewAsync(CancellationToken cancellationToken);
}
