using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Governance;

/// <summary>Immutable record of one destination write ("export") completing — CSV/SQL/S3/SFTP/etc.</summary>
public sealed class ExportHistory : Entity<Guid>, IAppendOnlyEntity
{
    private ExportHistory()
    {
    }

    public ExportHistory(
        Guid id,
        DateTime occurredOnUtc,
        Guid? pipelineRunId,
        string destinationName,
        string format,
        int rowCount,
        long? fileSizeBytes,
        string status,
        string? correlationId)
    {
        Id = id;
        OccurredOnUtc = occurredOnUtc;
        PipelineRunId = pipelineRunId;
        DestinationName = destinationName;
        Format = format;
        RowCount = rowCount;
        FileSizeBytes = fileSizeBytes;
        Status = status;
        CorrelationId = correlationId;
    }

    public DateTime OccurredOnUtc { get; private set; }
    public Guid? PipelineRunId { get; private set; }
    public string DestinationName { get; private set; } = default!;
    public string Format { get; private set; } = default!;
    public int RowCount { get; private set; }
    public long? FileSizeBytes { get; private set; }
    public string Status { get; private set; } = default!;
    public string? CorrelationId { get; private set; }
}
