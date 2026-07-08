using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>Furthest stage a resource reached within a route execution.</summary>
public static class PipelineResourceStage
{
    public const string Fetched = "Fetched";
    public const string Normalized = "Normalized";
    public const string Mapped = "Mapped";
    public const string Stored = "Stored";
    public const string Failed = "Failed";
}

public static class PipelineResourceWriteStatus
{
    public const string Written = "Written";
    public const string Skipped = "Skipped";
    public const string Failed = "Failed";
}

/// <summary>
/// Full fetch/normalize/map/store history for one resource within one <see cref="PipelineRunRouteExecution"/>.
/// Unlike <see cref="ResourceLineageEntry"/> (deliberately PHI-free ids/timestamps), this record holds the actual
/// payloads at each stage so a run's history can answer "what was fetched, what was mapped, what was stored" —
/// so <see cref="FetchedJson"/>, <see cref="NormalizedJson"/>, and <see cref="MappedValuesJson"/> are encrypted at
/// rest via <c>IPhiFieldEncryptor</c> (applied as an EF value converter) and purged under the retention policy.
/// </summary>
public sealed class PipelineRunResourceRecord : Entity<Guid>
{
    private PipelineRunResourceRecord()
    {
    }

    public PipelineRunResourceRecord(
        Guid id,
        Guid routeExecutionId,
        string resourceType,
        string? sourceResourceId,
        string fetchedJson,
        DateTime fetchedAtUtc)
    {
        Id = id;
        RouteExecutionId = routeExecutionId;
        ResourceType = resourceType;
        SourceResourceId = sourceResourceId;
        FetchedJson = fetchedJson;
        FetchedAtUtc = fetchedAtUtc;
        Stage = PipelineResourceStage.Fetched;
    }

    public Guid RouteExecutionId { get; private set; }
    public string ResourceType { get; private set; } = default!;
    public string? SourceResourceId { get; private set; }
    public string Stage { get; private set; } = default!;
    public string? ErrorMessage { get; private set; }

    public string FetchedJson { get; private set; } = default!;
    public DateTime FetchedAtUtc { get; private set; }

    public string? NormalizedJson { get; private set; }
    public string? AppliedProfiles { get; private set; }
    public string? Warnings { get; private set; }
    public double? DataQualityScore { get; private set; }
    public string? MasterPatientId { get; private set; }
    public DateTime? NormalizedAtUtc { get; private set; }

    public string? MappedValuesJson { get; private set; }
    public DateTime? MappedAtUtc { get; private set; }

    public DateTime? StoredAtUtc { get; private set; }
    public string? WriteStatus { get; private set; }

    public void MarkNormalized(
        string normalizedJson,
        string appliedProfilesJson,
        string warningsJson,
        double? dataQualityScore,
        string? masterPatientId,
        DateTime normalizedAtUtc)
    {
        NormalizedJson = normalizedJson;
        AppliedProfiles = appliedProfilesJson;
        Warnings = warningsJson;
        DataQualityScore = dataQualityScore;
        MasterPatientId = masterPatientId;
        NormalizedAtUtc = normalizedAtUtc;
        Stage = PipelineResourceStage.Normalized;
    }

    public void MarkMapped(string mappedValuesJson, DateTime mappedAtUtc)
    {
        MappedValuesJson = mappedValuesJson;
        MappedAtUtc = mappedAtUtc;
        Stage = PipelineResourceStage.Mapped;
    }

    public void MarkStored(DateTime storedAtUtc)
    {
        StoredAtUtc = storedAtUtc;
        WriteStatus = PipelineResourceWriteStatus.Written;
        Stage = PipelineResourceStage.Stored;
    }

    public void MarkFailed(string errorMessage)
    {
        ErrorMessage = errorMessage;
        WriteStatus = PipelineResourceWriteStatus.Failed;
        Stage = PipelineResourceStage.Failed;
    }
}
