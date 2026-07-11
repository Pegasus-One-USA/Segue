using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// Reads a sample of the rows a pipeline has written to a relational destination table, so the portal can show
/// "what data got stored". Read-only, capped, and connection failures are returned as <see cref="DestinationDataDto.Error"/>
/// rather than thrown.
/// </summary>
public interface IDestinationDataService
{
    Task<DestinationDataDto> ReadSampleAsync(
        Guid destinationId,
        string destinationObject,
        int top,
        IReadOnlyCollection<Guid> pipelineRunIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads every row matching an equality filter on an arbitrary column — for destination tables that don't carry
    /// a PipelineRunId (e.g. one-to-many child tables keyed only by a parent identifier like PatientId).
    /// </summary>
    Task<DestinationDataDto> ReadByColumnAsync(
        Guid destinationId,
        string destinationObject,
        string columnName,
        string columnValue,
        int top,
        CancellationToken cancellationToken);
}
