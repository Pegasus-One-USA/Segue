using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Real per-route Next Run/Last Run summary. Next Run is computed live (ScheduleExpressionMatcher.NextDueAfter);
/// Last Run/Duration/Status come from the route's most recent PipelineRunRouteExecution — the actual outcome,
/// not just a dispatch-decision log entry (which is what SchedulerHistory alone would give).
/// </summary>
public sealed class EfSchedulerSummaryService : ISchedulerSummaryService
{
    private readonly FHIRBridgeDbContext _dbContext;

    public EfSchedulerSummaryService(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<SchedulerSummaryDto>> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var routes = await _dbContext.ResourcePipelineRoutes
            .AsNoTracking()
            .Where(r => r.IsEnabled &&
                (r.IngestionMode == IngestionMode.ScheduledPull || r.IngestionMode == IngestionMode.WebhookAndScheduledPull) &&
                r.ScheduleExpression != null)
            .ToListAsync(cancellationToken);

        if (routes.Count == 0)
        {
            return [];
        }

        var mappingIds = routes.Select(r => r.MappingProfileId).Distinct().ToList();
        var mappingNamesById = await _dbContext.MappingProfiles
            .AsNoTracking()
            .Where(m => mappingIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Name })
            .ToDictionaryAsync(m => m.Id, m => m.Name, cancellationToken);

        var routeIds = routes.Select(r => r.Id).ToList();
        var latestExecutions = await _dbContext.PipelineRunRouteExecutions
            .AsNoTracking()
            .Where(e => routeIds.Contains(e.RouteId))
            .ToListAsync(cancellationToken);
        var latestByRouteId = latestExecutions
            .GroupBy(e => e.RouteId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.StartedOnUtc).First());

        var nowUtc = DateTime.UtcNow;

        return routes
            .Select(route =>
            {
                var name = mappingNamesById.GetValueOrDefault(route.MappingProfileId, "Unnamed route");
                var nextRunUtc = ScheduleExpressionMatcher.NextDueAfter(route.ScheduleExpression, nowUtc);
                var lastRun = latestByRouteId.GetValueOrDefault(route.Id);

                return new SchedulerSummaryDto(
                    route.Id,
                    name,
                    route.ScheduleExpression,
                    nextRunUtc,
                    lastRun?.StartedOnUtc,
                    lastRun?.CompletedOnUtc is { } completed && lastRun.StartedOnUtc != default
                        ? (long)(completed - lastRun.StartedOnUtc).TotalMilliseconds
                        : null,
                    lastRun?.Status);
            })
            .OrderBy(s => s.RouteName)
            .ToList();
    }
}
