using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// Reads a sample of the rows a pipeline has written to a relational destination table, so the portal can show
/// "what data got stored". Read-only, capped, and connection failures are returned as <see cref="DestinationDataDto.Error"/>
/// rather than thrown.
/// </summary>
public interface IDestinationDataService
{
    /// <summary>
    /// Top-N sample of the destination table. When the table carries a <c>PipelineRunId</c> column (FHIRBridge's
    /// own output tables, or any mapping that maps the <c>@runId</c> system value), the sample is scoped to
    /// <paramref name="pipelineRunIds"/> — the calling workflow's own runs — so it shows only what that workflow
    /// wrote; an empty set then yields no rows ("hasn't run yet"). A customer-owned table with no such column is
    /// read whole (the run ids are ignored), since there's nothing to filter on.
    /// </summary>
    Task<DestinationDataDto> ReadSampleAsync(
        Guid destinationId,
        string destinationObject,
        int top,
        IReadOnlyCollection<Guid> pipelineRunIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads every row matching an equality filter on an arbitrary column — e.g. one-to-many child tables keyed only
    /// by a parent identifier like PatientId.
    /// </summary>
    Task<DestinationDataDto> ReadByColumnAsync(
        Guid destinationId,
        string destinationObject,
        string columnName,
        string columnValue,
        int top,
        CancellationToken cancellationToken);
}
