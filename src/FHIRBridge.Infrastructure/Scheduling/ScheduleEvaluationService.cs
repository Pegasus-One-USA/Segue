using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Scheduling;
using FHIRBridge.Application.Scheduling;
using FHIRBridge.Domain.Aggregates;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Pipeline;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Scheduling;

/// <summary>
/// Multi-tenant generalization of the legacy single-tenant worker's due-route logic. Evaluates every tenant's
/// scheduled-pull routes with catch-up awareness, stamps each claimed route, and persists the stamp.
/// </summary>
public sealed class ScheduleEvaluationService : IScheduleEvaluationService
{
    private readonly ITenantConfigurationRepository _tenantRepository;
    private readonly ILogger<ScheduleEvaluationService> _logger;

    public ScheduleEvaluationService(
        ITenantConfigurationRepository tenantRepository,
        ILogger<ScheduleEvaluationService> logger)
    {
        _tenantRepository = tenantRepository;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DueScheduledRun>> ClaimDueRunsAsync(
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var tenants = await _tenantRepository.GetAllAsync(cancellationToken);
        var dueRuns = new List<DueScheduledRun>();

        foreach (var tenant in tenants)
        {
            var dueResourceTypes = new List<string>();
            var dueRouteIds = new List<Guid>();

            foreach (var route in tenant.ResourcePipelineRoutes)
            {
                if (!route.IsEnabled ||
                    !IsScheduledPullMode(route.IngestionMode) ||
                    !RouteDependenciesAreEnabled(tenant, route) ||
                    !ScheduleExpressionMatcher.IsDueSince(route.ScheduleExpression, route.LastTriggeredOnUtc, utcNow))
                {
                    continue;
                }

                route.MarkTriggered(utcNow);
                dueRouteIds.Add(route.Id);

                // Resource type is owned by the route's mapping profile (single source of truth).
                var resourceType = tenant.ResolveResourceType(route);
                if (!string.IsNullOrWhiteSpace(resourceType))
                {
                    dueResourceTypes.Add(resourceType);
                }
            }

            if (dueRouteIds.Count > 0)
            {
                dueRuns.Add(new DueScheduledRun(
                    tenant.Id,
                    dueResourceTypes.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList(),
                    dueRouteIds));
            }

            if (dueRouteIds.Count > 0)
            {
                await _tenantRepository.UpdateAsync(tenant, cancellationToken);
                _logger.LogInformation(
                    "Claimed {ResourceTypeCount} due scheduled resource type(s) for tenant {TenantId} at {UtcNow}.",
                    dueResourceTypes.Count,
                    tenant.Id,
                    utcNow);
            }
        }

        return dueRuns;
    }

    private static bool IsScheduledPullMode(IngestionMode ingestionMode)
    {
        return ingestionMode is IngestionMode.ScheduledPull or IngestionMode.WebhookAndScheduledPull;
    }

    private static bool RouteDependenciesAreEnabled(Tenant tenant, ResourcePipelineRoute route)
    {
        // A route's source and destination are owned by its mapping profile, so resolve them through the mapping.
        var mapping = tenant.MappingProfiles.FirstOrDefault(x => x.Id == route.MappingProfileId);
        if (mapping is null)
        {
            return false;
        }

        var source = tenant.SourceConnections.FirstOrDefault(x => x.Id == mapping.SourceConnectionId);
        var destination = tenant.DestinationConfigurations.FirstOrDefault(x => x.Id == mapping.DestinationId);
        var webhook = route.WebhookConfigurationId.HasValue
            ? tenant.WebhookConfigurations.FirstOrDefault(x => x.Id == route.WebhookConfigurationId.Value)
            : null;

        return source?.IsEnabled == true &&
               destination?.IsEnabled == true &&
               mapping.IsEnabled &&
               (!route.WebhookConfigurationId.HasValue || webhook?.IsEnabled == true);
    }
}
