using System.Text.Json;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Pipeline;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>
/// Durable, EF-backed fetch/normalize/map/store history per resource. <see cref="PipelineRunResourceRecord.FetchedJson"/>,
/// <see cref="PipelineRunResourceRecord.NormalizedJson"/>, and <see cref="PipelineRunResourceRecord.MappedValuesJson"/>
/// are encrypted with <see cref="IPhiFieldEncryptor"/> before they touch the entity, so the PHI they carry is
/// encrypted at rest. Implements <see cref="IPurgeableStore"/> so it is swept by the existing retention purge job.
/// </summary>
public sealed class EfExecutionResourceHistoryRecorder : IExecutionResourceHistoryRecorder, IPurgeableStore
{
    private readonly FHIRBridgeDbContext _dbContext;
    private readonly IPhiFieldEncryptor _encryptor;

    public EfExecutionResourceHistoryRecorder(FHIRBridgeDbContext dbContext, IPhiFieldEncryptor encryptor)
    {
        _dbContext = dbContext;
        _encryptor = encryptor;
    }

    public string DataClass => "PipelineRunResourceHistory";

    public async Task RecordFetchedAsync(
        Guid routeExecutionId,
        string resourceType,
        string? sourceResourceId,
        string fetchedJson,
        CancellationToken cancellationToken)
    {
        var record = new PipelineRunResourceRecord(
            Guid.NewGuid(),
            routeExecutionId,
            resourceType,
            sourceResourceId,
            _encryptor.Encrypt(fetchedJson),
            DateTime.UtcNow);

        _dbContext.PipelineRunResourceRecords.Add(record);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordNormalizedAsync(
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
        var record = await FindLatestAsync(routeExecutionId, resourceType, sourceResourceId, cancellationToken);
        if (record is null)
        {
            return;
        }

        record.MarkNormalized(
            _encryptor.Encrypt(normalizedJson),
            JsonSerializer.Serialize(appliedProfiles),
            JsonSerializer.Serialize(warnings),
            dataQualityScore,
            masterPatientId,
            DateTime.UtcNow);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordMappedAsync(
        Guid routeExecutionId,
        string resourceType,
        string? sourceResourceId,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken)
    {
        var record = await FindLatestAsync(routeExecutionId, resourceType, sourceResourceId, cancellationToken);
        if (record is null)
        {
            return;
        }

        record.MarkMapped(_encryptor.Encrypt(JsonSerializer.Serialize(values)), DateTime.UtcNow);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordStoredAsync(
        Guid routeExecutionId,
        IReadOnlyCollection<string?> sourceResourceIds,
        CancellationToken cancellationToken)
    {
        var ids = sourceResourceIds.Where(id => id is not null).ToHashSet();
        var records = await _dbContext.PipelineRunResourceRecords
            .Where(x => x.RouteExecutionId == routeExecutionId && x.SourceResourceId != null && ids.Contains(x.SourceResourceId))
            .ToListAsync(cancellationToken);

        var storedAtUtc = DateTime.UtcNow;
        foreach (var record in records)
        {
            record.MarkStored(storedAtUtc);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordFailedAsync(
        Guid routeExecutionId,
        string resourceType,
        string? sourceResourceId,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var record = await FindLatestAsync(routeExecutionId, resourceType, sourceResourceId, cancellationToken);
        if (record is null)
        {
            return;
        }

        record.MarkFailed(errorMessage);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<PagedResult<PipelineRunResourceHistoryDto>> GetPagedAsync(
        Guid routeExecutionId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = _dbContext.PipelineRunResourceRecords
            .AsNoTracking()
            .Where(x => x.RouteExecutionId == routeExecutionId);

        var totalCount = await query.CountAsync(cancellationToken);

        var take = Math.Clamp(pageSize, 1, 200);
        var skip = Math.Max(0, (page - 1) * take);

        var records = await query
            .OrderBy(x => x.FetchedAtUtc)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return new PagedResult<PipelineRunResourceHistoryDto>(
            records.Select(ToDto).ToList(),
            totalCount,
            page,
            take);
    }

    public async Task<int> PurgeOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        var expired = await _dbContext.PipelineRunResourceRecords
            .Where(x => x.FetchedAtUtc < cutoffUtc)
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
        {
            return 0;
        }

        _dbContext.PipelineRunResourceRecords.RemoveRange(expired);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return expired.Count;
    }

    // Resources are recorded once (at "Fetched") and updated in place through later stages, all within the same
    // DbContext scope (one per pipeline run), so this is a change-tracker lookup rather than a query for the
    // common case; falls back to the database for the rare null-SourceResourceId or cross-scope case.
    private async Task<PipelineRunResourceRecord?> FindLatestAsync(
        Guid routeExecutionId,
        string resourceType,
        string? sourceResourceId,
        CancellationToken cancellationToken)
    {
        if (sourceResourceId is null)
        {
            return null;
        }

        var tracked = _dbContext.ChangeTracker.Entries<PipelineRunResourceRecord>()
            .Select(e => e.Entity)
            .FirstOrDefault(x =>
                x.RouteExecutionId == routeExecutionId &&
                x.ResourceType == resourceType &&
                x.SourceResourceId == sourceResourceId);

        if (tracked is not null)
        {
            return tracked;
        }

        return await _dbContext.PipelineRunResourceRecords.FirstOrDefaultAsync(
            x => x.RouteExecutionId == routeExecutionId &&
                 x.ResourceType == resourceType &&
                 x.SourceResourceId == sourceResourceId,
            cancellationToken);
    }

    private PipelineRunResourceHistoryDto ToDto(PipelineRunResourceRecord record)
    {
        return new PipelineRunResourceHistoryDto(
            record.Id,
            record.RouteExecutionId,
            record.ResourceType,
            record.SourceResourceId,
            record.Stage,
            record.ErrorMessage,
            _encryptor.Decrypt(record.FetchedJson),
            record.FetchedAtUtc,
            record.NormalizedJson is null ? null : _encryptor.Decrypt(record.NormalizedJson),
            record.AppliedProfiles is null ? [] : JsonSerializer.Deserialize<IReadOnlyList<string>>(record.AppliedProfiles) ?? [],
            record.Warnings is null ? [] : JsonSerializer.Deserialize<IReadOnlyList<string>>(record.Warnings) ?? [],
            record.DataQualityScore,
            record.MasterPatientId,
            record.NormalizedAtUtc,
            record.MappedValuesJson is null ? null : _encryptor.Decrypt(record.MappedValuesJson),
            record.MappedAtUtc,
            record.StoredAtUtc,
            record.WriteStatus);
    }
}
