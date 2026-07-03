using System.Text.Json;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfConfiguredPipelineRunRepository : IConfiguredPipelineRunRepository
{
    private readonly FHIRBridgeDbContext _dbContext;

    public EfConfiguredPipelineRunRepository(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(
        ConfiguredPipelineRunDto pipelineRun,
        CancellationToken cancellationToken)
    {
        var record = new ConfiguredPipelineRunRecord(
            pipelineRun.Id,
            pipelineRun.Status,
            JsonSerializer.Serialize(pipelineRun.ResourceTypes),
            pipelineRun.ExtractedResourceCount,
            pipelineRun.MappedRecordCount,
            pipelineRun.WrittenRecordCount,
            JsonSerializer.Serialize(pipelineRun.Errors),
            pipelineRun.StartedOnUtc,
            pipelineRun.CompletedOnUtc,
            pipelineRun.TriggeredBy,
            pipelineRun.TriggerType);
        record.SetEnabled(pipelineRun.IsEnabled);

        _dbContext.ConfiguredPipelineRuns.Add(record);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ConfiguredPipelineRunDto>> GetRecentAsync(
        int count,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(count, 1, 500);
        var records = await _dbContext.ConfiguredPipelineRuns
            .AsNoTracking()
            .Where(x => x.IsEnabled)
            .OrderByDescending(x => x.StartedOnUtc)
            .Take(take)
            .ToListAsync(cancellationToken);

        return records.Select(ToDto).ToList();
    }

    public async Task SetEnabledAsync(
        Guid pipelineRunId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var record = await _dbContext.ConfiguredPipelineRuns
            .FirstOrDefaultAsync(x => x.Id == pipelineRunId, cancellationToken);
        if (record is null)
        {
            return;
        }

        record.SetEnabled(isEnabled);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static ConfiguredPipelineRunDto ToDto(ConfiguredPipelineRunRecord record)
    {
        return new ConfiguredPipelineRunDto(
            record.Id,
            record.Status,
            JsonSerializer.Deserialize<IReadOnlyList<string>>(record.ResourceTypes) ?? [],
            record.ExtractedResourceCount,
            record.MappedRecordCount,
            record.WrittenRecordCount,
            JsonSerializer.Deserialize<IReadOnlyList<string>>(record.Errors) ?? [],
            record.StartedOnUtc,
            record.CompletedOnUtc,
            record.IsEnabled,
            record.TriggeredBy,
            record.TriggerType);
    }
}
