using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Pipeline;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class InMemoryExecutionResourceHistoryRecorder : IExecutionResourceHistoryRecorder
{
    private sealed record Row(
        Guid Id,
        Guid RouteExecutionId,
        string ResourceType,
        string? SourceResourceId)
    {
        public string Stage { get; set; } = PipelineResourceStage.Fetched;
        public string? ErrorMessage { get; set; }
        public string FetchedJson { get; set; } = default!;
        public DateTime FetchedAtUtc { get; set; }
        public string? NormalizedJson { get; set; }
        public IReadOnlyList<string> AppliedProfiles { get; set; } = [];
        public IReadOnlyList<string> Warnings { get; set; } = [];
        public double? DataQualityScore { get; set; }
        public string? MasterPatientId { get; set; }
        public DateTime? NormalizedAtUtc { get; set; }
        public string? MappedValuesJson { get; set; }
        public DateTime? MappedAtUtc { get; set; }
        public DateTime? StoredAtUtc { get; set; }
        public string? WriteStatus { get; set; }
    }

    private readonly List<Row> _rows = [];
    private readonly object _gate = new();

    public Task RecordFetchedAsync(
        Guid routeExecutionId,
        string resourceType,
        string? sourceResourceId,
        string fetchedJson,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _rows.Add(new Row(Guid.NewGuid(), routeExecutionId, resourceType, sourceResourceId)
            {
                FetchedJson = fetchedJson,
                FetchedAtUtc = DateTime.UtcNow,
            });
        }

        return Task.CompletedTask;
    }

    public Task RecordNormalizedAsync(
        Guid routeExecutionId,
        string resourceType,
        string? sourceResourceId,
        string normalizedJson,
        IReadOnlyCollection<string> appliedProfiles,
        IReadOnlyCollection<string> warnings,
        double? dataQualityScore,
        string? masterPatientId,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var row = Find(routeExecutionId, resourceType, sourceResourceId);
            if (row is null)
            {
                return Task.CompletedTask;
            }

            row.NormalizedJson = normalizedJson;
            row.AppliedProfiles = appliedProfiles.ToList();
            row.Warnings = warnings.ToList();
            row.DataQualityScore = dataQualityScore;
            row.MasterPatientId = masterPatientId;
            row.NormalizedAtUtc = DateTime.UtcNow;
            row.Stage = PipelineResourceStage.Normalized;
        }

        return Task.CompletedTask;
    }

    public Task RecordMappedAsync(
        Guid routeExecutionId,
        string resourceType,
        string? sourceResourceId,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var row = Find(routeExecutionId, resourceType, sourceResourceId);
            if (row is null)
            {
                return Task.CompletedTask;
            }

            row.MappedValuesJson = System.Text.Json.JsonSerializer.Serialize(values);
            row.MappedAtUtc = DateTime.UtcNow;
            row.Stage = PipelineResourceStage.Mapped;
        }

        return Task.CompletedTask;
    }

    public Task RecordStoredAsync(
        Guid routeExecutionId,
        IReadOnlyCollection<string?> sourceResourceIds,
        CancellationToken cancellationToken)
    {
        var ids = sourceResourceIds.Where(id => id is not null).ToHashSet();

        lock (_gate)
        {
            foreach (var row in _rows.Where(x => x.RouteExecutionId == routeExecutionId && ids.Contains(x.SourceResourceId)))
            {
                row.StoredAtUtc = DateTime.UtcNow;
                row.WriteStatus = PipelineResourceWriteStatus.Written;
                row.Stage = PipelineResourceStage.Stored;
            }
        }

        return Task.CompletedTask;
    }

    public Task RecordFailedAsync(
        Guid routeExecutionId,
        string resourceType,
        string? sourceResourceId,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var row = Find(routeExecutionId, resourceType, sourceResourceId);
            if (row is null)
            {
                return Task.CompletedTask;
            }

            row.ErrorMessage = errorMessage;
            row.WriteStatus = PipelineResourceWriteStatus.Failed;
            row.Stage = PipelineResourceStage.Failed;
        }

        return Task.CompletedTask;
    }

    public Task<PagedResult<PipelineRunResourceHistoryDto>> GetPagedAsync(
        Guid routeExecutionId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var matching = _rows.Where(x => x.RouteExecutionId == routeExecutionId)
                .OrderBy(x => x.FetchedAtUtc)
                .ToList();

            var take = Math.Clamp(pageSize, 1, 200);
            var skip = Math.Max(0, (page - 1) * take);

            var items = matching.Skip(skip).Take(take).Select(row => new PipelineRunResourceHistoryDto(
                row.Id,
                row.RouteExecutionId,
                row.ResourceType,
                row.SourceResourceId,
                row.Stage,
                row.ErrorMessage,
                row.FetchedAtUtc,
                row.AppliedProfiles,
                row.Warnings,
                row.DataQualityScore,
                row.MasterPatientId,
                row.NormalizedAtUtc,
                row.MappedAtUtc,
                row.StoredAtUtc,
                row.WriteStatus)).ToList();

            return Task.FromResult(new PagedResult<PipelineRunResourceHistoryDto>(items, matching.Count, page, take));
        }
    }

    private Row? Find(Guid routeExecutionId, string resourceType, string? sourceResourceId)
    {
        if (sourceResourceId is null)
        {
            return null;
        }

        return _rows.FirstOrDefault(x =>
            x.RouteExecutionId == routeExecutionId &&
            x.ResourceType == resourceType &&
            x.SourceResourceId == sourceResourceId);
    }
}
