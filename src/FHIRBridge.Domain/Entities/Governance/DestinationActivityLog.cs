using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Governance;

/// <summary>
/// Immutable record of one stage of one destination write: opening the connection, and the write completing or
/// failing. Exists because <c>ApiRequestLoggingHandler</c> — which produces the whole API Requests trace — is an
/// <c>HttpClient</c> <c>DelegatingHandler</c>, so it sees only destinations that speak HTTP. Most do not
/// (ADO.NET for SQL Server/PostgreSQL/MySQL, vendor SDKs for SFTP/S3/Blob/Fabric, the Mongo driver), which left
/// every one of those invisible in a correlation trace no matter how the write went.
/// <para>Deliberately NOT stored in <see cref="ApiRequestLog"/>: <c>Method</c>/<c>Url</c>/<c>StatusCode</c> are
/// meaningless for an ADO.NET connect, and filling them with placeholders would corrupt the API Requests screen
/// and the analytics built on it.</para>
/// <para><b>Never holds PHI.</b> Counts, identifiers and timings only — a <c>MappedDestinationRecord</c> carries
/// mapped patient data and must never reach this table. The PHI-masking Serilog enricher is a backstop for
/// accidents, not a licence to hand it PHI deliberately.</para>
/// </summary>
public sealed class DestinationActivityLog : Entity<Guid>, IAppendOnlyEntity
{
    private DestinationActivityLog()
    {
    }

    public DestinationActivityLog(
        Guid id,
        DateTime occurredOnUtc,
        Guid destinationId,
        string destinationName,
        string destinationType,
        string stage,
        string status,
        string? resourceType,
        int? recordCount,
        int? writtenCount,
        long durationMs,
        string? detail,
        string? error,
        string? correlationId,
        Guid? pipelineRunId)
    {
        Id = id;
        OccurredOnUtc = occurredOnUtc;
        DestinationId = destinationId;
        DestinationName = destinationName;
        DestinationType = destinationType;
        Stage = stage;
        Status = status;
        ResourceType = resourceType;
        RecordCount = recordCount;
        WrittenCount = writtenCount;
        DurationMs = durationMs;
        Detail = detail;
        Error = error;
        CorrelationId = correlationId;
        PipelineRunId = pipelineRunId;
    }

    public DateTime OccurredOnUtc { get; private set; }
    public Guid DestinationId { get; private set; }
    public string DestinationName { get; private set; } = default!;
    public string DestinationType { get; private set; } = default!;

    /// <summary>One of <see cref="DestinationActivityStage"/>.</summary>
    public string Stage { get; private set; } = default!;

    /// <summary>One of <see cref="DestinationActivityStatus"/>.</summary>
    public string Status { get; private set; } = default!;

    public string? ResourceType { get; private set; }
    public int? RecordCount { get; private set; }
    public int? WrittenCount { get; private set; }
    public long DurationMs { get; private set; }

    /// <summary>Short, PHI-free context for the stage — e.g. which half of a two-part Fabric connect this was, or
    /// the qualified table a write targeted. Never record data.</summary>
    public string? Detail { get; private set; }

    public string? Error { get; private set; }
    public string? CorrelationId { get; private set; }
    public Guid? PipelineRunId { get; private set; }
}

/// <summary>The values <see cref="DestinationActivityLog.Stage"/> takes.</summary>
public static class DestinationActivityStage
{
    /// <summary>Opening/authenticating the connection to the destination. Only emitted where such a step genuinely
    /// exists — a CSV or Parquet writer resolves a path and has nothing to connect to.</summary>
    public const string Connect = "Connect";

    /// <summary>The write finished — succeeded, partially succeeded, or failed.</summary>
    public const string Complete = "Complete";
}

/// <summary>The values <see cref="DestinationActivityLog.Status"/> takes.</summary>
public static class DestinationActivityStatus
{
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";

    /// <summary>The write completed but rejected some records — see <c>DestinationWriteResult.RecordErrors</c>.</summary>
    public const string PartialSuccess = "PartialSuccess";

    /// <summary>The write ran with nothing to write.</summary>
    public const string NoData = "NoData";
}
