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
/// This record holds the actual payloads at each stage so a run's history can answer "what was fetched, what
/// was mapped, what was stored" — so <see cref="FetchedJson"/>, <see cref="NormalizedJson"/>, and
/// <see cref="MappedValuesJson"/> are encrypted at rest via <c>IPhiFieldEncryptor</c> (applied as an EF value
/// converter) and purged under the retention policy.
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
        DateTime fetchedAtUtc)
    {
        Id = id;
        RouteExecutionId = routeExecutionId;
        ResourceType = resourceType;
        SourceResourceId = sourceResourceId;
        FetchedAtUtc = fetchedAtUtc;
        Stage = PipelineResourceStage.Fetched;
    }

    public Guid RouteExecutionId { get; private set; }
    public string ResourceType { get; private set; } = default!;
    public string? SourceResourceId { get; private set; }
    public string Stage { get; private set; } = default!;
    public string? ErrorMessage { get; private set; }

    // FetchedJson/NormalizedJson/MappedValuesJson removed: they held whole fetched FHIR resources and mapped
    // field values. Encrypting them at rest did not stop them being retained, decryptable PHI. What remains
    // below is the stage/quality/timing story — which resource reached which stage, when, and how well — which
    // is what this record exists to answer.
    public DateTime FetchedAtUtc { get; private set; }

    public string? AppliedProfiles { get; private set; }
    public string? Warnings { get; private set; }
    public double? DataQualityScore { get; private set; }
    public string? MasterPatientId { get; private set; }
    public DateTime? NormalizedAtUtc { get; private set; }

    public DateTime? MappedAtUtc { get; private set; }

    public DateTime? StoredAtUtc { get; private set; }
    public string? WriteStatus { get; private set; }

    public void MarkNormalized(
        string appliedProfilesJson,
        string warningsJson,
        double? dataQualityScore,
        string? masterPatientId,
        DateTime normalizedAtUtc)
    {
        AppliedProfiles = appliedProfilesJson;
        Warnings = warningsJson;
        DataQualityScore = dataQualityScore;
        MasterPatientId = masterPatientId;
        NormalizedAtUtc = normalizedAtUtc;
        Stage = PipelineResourceStage.Normalized;
    }

    public void MarkMapped(DateTime mappedAtUtc)
    {
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
