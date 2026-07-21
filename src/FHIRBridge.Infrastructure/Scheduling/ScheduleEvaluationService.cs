using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Scheduling;
using FHIRBridge.Application.Scheduling;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Pipeline;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Scheduling;

/// <summary>
/// Single-org due-route logic. Evaluates the configured scheduled-pull routes with catch-up awareness, stamps each
/// claimed route, and persists the stamp.
/// </summary>
public sealed class ScheduleEvaluationService : IScheduleEvaluationService
{
    private readonly IConfigurationRepository _configurationRepository;
    private readonly ILogger<ScheduleEvaluationService> _logger;

    public ScheduleEvaluationService(
        IConfigurationRepository configurationRepository,
        ILogger<ScheduleEvaluationService> logger)
    {
        _configurationRepository = configurationRepository;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DueScheduledRun>> ClaimDueRunsAsync(
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var routes = await _configurationRepository.GetRoutesAsync(cancellationToken);
        var mappings = (await _configurationRepository.GetMappingProfilesAsync(cancellationToken))
            .ToDictionary(x => x.Id);
        var sources = (await _configurationRepository.GetSourceConnectionsAsync(cancellationToken))
            .ToDictionary(x => x.Id);
        var destinations = (await _configurationRepository.GetDestinationsAsync(cancellationToken))
            .ToDictionary(x => x.Id);
        var webhooks = (await _configurationRepository.GetWebhooksAsync(cancellationToken))
            .ToDictionary(x => x.Id);

        var dueResourceTypes = new List<string>();
        var dueRouteIds = new List<Guid>();
        var dueRouteLabels = new List<string>();
        var claimedRoutes = new List<ResourcePipelineRoute>();

        foreach (var route in routes)
        {
            if (!route.IsEnabled ||
                !IsScheduledPullMode(route.IngestionMode) ||
                !RouteDependenciesAreEnabled(route, mappings, sources, destinations, webhooks) ||
                !ScheduleExpressionMatcher.IsDueSince(route.ScheduleExpression, route.LastTriggeredOnUtc, utcNow))
            {
                continue;
            }

            route.MarkTriggered(utcNow);
            dueRouteIds.Add(route.Id);
            claimedRoutes.Add(route);

            // Resource type is owned by the route's mapping profile (single source of truth).
            if (mappings.TryGetValue(route.MappingProfileId, out var mapping) &&
                !string.IsNullOrWhiteSpace(mapping.ResourceType))
            {
                dueResourceTypes.Add(mapping.ResourceType);
                dueRouteLabels.Add(!string.IsNullOrWhiteSpace(mapping.Name) ? mapping.Name : mapping.ResourceType);
            }
            else
            {
                dueRouteLabels.Add(route.Id.ToString("N"));
            }
        }

        if (dueRouteIds.Count == 0)
        {
            return [];
        }

        foreach (var route in claimedRoutes)
        {
            await _configurationRepository.UpdateRouteAsync(route, cancellationToken);
        }

        _logger.LogInformation(
            "Claimed {ResourceTypeCount} due scheduled resource type(s) at {UtcNow}.",
            dueResourceTypes.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            utcNow);

        return
        [
            new DueScheduledRun(
                dueResourceTypes.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList(),
                dueRouteIds)
            {
                RouteLabels = dueRouteLabels,
            }
        ];
    }

    private static bool IsScheduledPullMode(IngestionMode ingestionMode)
    {
        return ingestionMode is IngestionMode.ScheduledPull or IngestionMode.WebhookAndScheduledPull;
    }

    private static bool RouteDependenciesAreEnabled(
        ResourcePipelineRoute route,
        IReadOnlyDictionary<Guid, MappingProfile> mappings,
        IReadOnlyDictionary<Guid, SourceConnection> sources,
        IReadOnlyDictionary<Guid, DestinationConfiguration> destinations,
        IReadOnlyDictionary<Guid, WebhookConfiguration> webhooks)
    {
        // A route's source and destination are owned by its mapping profile, so resolve them through the mapping.
        if (!mappings.TryGetValue(route.MappingProfileId, out var mapping))
        {
            return false;
        }

        sources.TryGetValue(mapping.SourceConnectionId, out var source);
        destinations.TryGetValue(mapping.DestinationId, out var destination);
        WebhookConfiguration? webhook = null;
        if (route.WebhookConfigurationId.HasValue)
        {
            webhooks.TryGetValue(route.WebhookConfigurationId.Value, out webhook);
        }

        return source?.IsEnabled == true &&
               destination?.IsEnabled == true &&
               mapping.IsEnabled &&
               (!route.WebhookConfigurationId.HasValue || webhook?.IsEnabled == true);
    }
}
