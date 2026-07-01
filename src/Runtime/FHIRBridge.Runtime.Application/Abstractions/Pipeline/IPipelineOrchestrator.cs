using FHIRBridge.Runtime.Application.DTOs;

namespace FHIRBridge.Runtime.Application.Abstractions.Pipeline;

public interface IPipelineOrchestrator
{
    Task<PipelineRunDto> StartAsync(
        StartPipelineRunRequest request,
        CancellationToken cancellationToken);

    /// <summary>Runs a pipeline whose resources come from a FHIR Bulk Data <c>$export</c> instead of a search.</summary>
    Task<PipelineRunDto> StartBulkExportAsync(
        StartBulkExportRunRequest request,
        CancellationToken cancellationToken);
}
