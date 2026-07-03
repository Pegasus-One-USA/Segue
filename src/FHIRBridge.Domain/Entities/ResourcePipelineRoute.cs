using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

public sealed class ResourcePipelineRoute : AuditableChildEntity<Guid>
{
    private ResourcePipelineRoute()
    {
    }

    public ResourcePipelineRoute(
        Guid? webhookConfigurationId,
        Guid mappingProfileId,
        IngestionMode ingestionMode,
        string? scheduleExpression,
        string? searchParameters,
        bool isEnabled,
        int priority)
    {
        Id = Guid.NewGuid();
        WebhookConfigurationId = webhookConfigurationId;
        MappingProfileId = mappingProfileId;
        IngestionMode = ingestionMode;
        ScheduleExpression = scheduleExpression;
        SearchParameters = searchParameters;
        IsEnabled = isEnabled;
        Priority = priority;
    }

    public Guid? WebhookConfigurationId { get; private set; }

    /// <summary>
    /// The mapping that transforms this route's resources. It is the single source of truth for which FHIR resource
    /// this route extracts (<c>ResourceType</c>), which source it ingests from (<c>SourceConnectionId</c>), and which
    /// destination it writes to (<c>DestinationId</c>) — there are no separate columns for those on the route.
    /// </summary>
    public Guid MappingProfileId { get; private set; }
    public IngestionMode IngestionMode { get; private set; }
    public string? ScheduleExpression { get; private set; }
    public string? SearchParameters { get; private set; }
    public bool IsEnabled { get; private set; }
    public int Priority { get; private set; }

    /// <summary>
    /// Last time the scheduler claimed this route for a run. Used by catch-up scheduling to detect missed slots
    /// and to avoid re-firing the same slot. Null means the route has never been triggered.
    /// </summary>
    public DateTime? LastTriggeredOnUtc { get; private set; }

    public void Update(
        IngestionMode ingestionMode,
        Guid? webhookConfigurationId,
        Guid mappingProfileId,
        string? scheduleExpression,
        string? searchParameters,
        bool isEnabled,
        int priority)
    {
        IngestionMode = ingestionMode;
        WebhookConfigurationId = webhookConfigurationId;
        MappingProfileId = mappingProfileId;
        ScheduleExpression = scheduleExpression;
        SearchParameters = searchParameters;
        IsEnabled = isEnabled;
        Priority = priority;
    }

    public void SetEnabled(bool isEnabled)
    {
        IsEnabled = isEnabled;
    }

    /// <summary>Records that the scheduler claimed this route for a run at <paramref name="triggeredOnUtc"/>.</summary>
    public void MarkTriggered(DateTime triggeredOnUtc)
    {
        LastTriggeredOnUtc = triggeredOnUtc;
    }
}
