using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfPipelineRunRouteExecutionRepository : IPipelineRunRouteExecutionRepository
{
    private readonly FHIRBridgeDbContext _dbContext;

    public EfPipelineRunRouteExecutionRepository(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Guid> CreateRunningAsync(
        Guid pipelineRunId,
        Guid routeId,
        Guid mappingProfileId,
        string pipelineName,
        Guid sourceConnectionId,
        string sourceName,
        string sourceSystemType,
        string? triggeredBy,
        string? triggerType,
        DateTime startedOnUtc,
        CancellationToken cancellationToken)
    {
        var execution = new PipelineRunRouteExecution(
            Guid.NewGuid(),
            pipelineRunId,
            routeId,
            mappingProfileId,
            pipelineName,
            sourceConnectionId,
            sourceName,
            sourceSystemType,
            triggeredBy,
            triggerType,
            startedOnUtc);

        _dbContext.PipelineRunRouteExecutions.Add(execution);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return execution.Id;
    }

    public async Task CompleteAsync(
        Guid routeExecutionId,
        string status,
        int extractedCount,
        int mappedCount,
        int writtenCount,
        string? errorMessage,
        DateTime completedOnUtc,
        CancellationToken cancellationToken)
    {
        var execution = await _dbContext.PipelineRunRouteExecutions
            .FirstOrDefaultAsync(x => x.Id == routeExecutionId, cancellationToken);
        if (execution is null)
        {
            return;
        }

        execution.Complete(status, extractedCount, mappedCount, writtenCount, errorMessage, completedOnUtc);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<PagedResult<PipelineRunRouteExecutionDto>> GetPagedAsync(
        PipelineRunRouteExecutionFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = _dbContext.PipelineRunRouteExecutions.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            query = query.Where(x => x.Status == filter.Status);
        }

        if (!string.IsNullOrWhiteSpace(filter.Source))
        {
            query = query.Where(x => x.SourceName == filter.Source || x.SourceSystemType == filter.Source);
        }

        if (!string.IsNullOrWhiteSpace(filter.TriggeredBy))
        {
            query = query.Where(x => x.TriggeredBy == filter.TriggeredBy || x.TriggerType == filter.TriggeredBy);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search;
            query = query.Where(x =>
                EF.Functions.Like(x.PipelineName, $"%{search}%") ||
                EF.Functions.Like(x.SourceName, $"%{search}%"));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var take = Math.Clamp(pageSize, 1, 200);
        var skip = Math.Max(0, (page - 1) * take);

        var records = await query
            .OrderByDescending(x => x.StartedOnUtc)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return new PagedResult<PipelineRunRouteExecutionDto>(
            records.Select(ToDto).ToList(),
            totalCount,
            page,
            take);
    }

    public async Task<PipelineRunRouteExecutionDto?> GetByIdAsync(
        Guid routeExecutionId,
        CancellationToken cancellationToken)
    {
        var execution = await _dbContext.PipelineRunRouteExecutions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == routeExecutionId, cancellationToken);

        return execution is null ? null : ToDto(execution);
    }

    private static PipelineRunRouteExecutionDto ToDto(PipelineRunRouteExecution execution)
    {
        return new PipelineRunRouteExecutionDto(
            execution.Id,
            execution.PipelineRunId,
            execution.PipelineName,
            execution.SourceName,
            execution.SourceSystemType,
            execution.Status,
            execution.StartedOnUtc,
            execution.CompletedOnUtc,
            execution.TriggeredBy,
            execution.TriggerType,
            execution.ExtractedCount,
            execution.MappedCount,
            execution.WrittenCount,
            execution.ErrorMessage);
    }
}
