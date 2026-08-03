using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

public sealed class ResourcePipelineRoute : AuditableChildEntity<Guid>
{
    private readonly List<ResourcePipelineRouteMapping> _resourceMappings = new();

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
        int priority,
        string timeZoneId = "UTC")
    {
        Id = Guid.NewGuid();
        WebhookConfigurationId = webhookConfigurationId;
        MappingProfileId = mappingProfileId;
        IngestionMode = ingestionMode;
        ScheduleExpression = scheduleExpression;
        SearchParameters = searchParameters;
        IsEnabled = isEnabled;
        Priority = priority;
        TimeZoneId = timeZoneId;
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

    /// <summary>
    /// IANA time zone identifier (e.g. <c>"America/New_York"</c>) that <c>ScheduleExpression</c> is evaluated in.
    /// Defaults to <c>"UTC"</c> for routes that predate this field.
    /// </summary>
    public string TimeZoneId { get; private set; } = "UTC";
    public string? SearchParameters { get; private set; }
    public bool IsEnabled { get; private set; }
    public int Priority { get; private set; }
    public IReadOnlyCollection<ResourcePipelineRouteMapping> ResourceMappings => _resourceMappings;

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
        int priority,
        string timeZoneId = "UTC")
    {
        IngestionMode = ingestionMode;
        WebhookConfigurationId = webhookConfigurationId;
        MappingProfileId = mappingProfileId;
        ScheduleExpression = scheduleExpression;
        SearchParameters = searchParameters;
        IsEnabled = isEnabled;
        Priority = priority;
        TimeZoneId = timeZoneId;
    }

    public void ReplaceResourceMappings(IEnumerable<ResourcePipelineRouteMapping> resourceMappings)
    {
        _resourceMappings.Clear();
        _resourceMappings.AddRange(resourceMappings.OrderBy(x => x.ExecutionOrder).ThenBy(x => x.MappingProfileId));
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

public sealed class ResourcePipelineRouteMapping
{
    private readonly List<ParentReferenceLink> _parentReferences = new();

    private ResourcePipelineRouteMapping()
    {
    }

    public ResourcePipelineRouteMapping(
        Guid mappingProfileId,
        bool isEnabled,
        int executionOrder,
        string? searchParameters = null)
    {
        Id = Guid.NewGuid();
        MappingProfileId = mappingProfileId;
        IsEnabled = isEnabled;
        ExecutionOrder = executionOrder;
        SearchParameters = searchParameters;
    }

    public Guid Id { get; private set; }
    public Guid ResourcePipelineRouteId { get; private set; }
    public Guid MappingProfileId { get; private set; }
    public bool IsEnabled { get; private set; }
    public int ExecutionOrder { get; private set; }

    /// <summary>
    /// Optional FHIR search parameters for this specific resource mapping, overriding the route's search parameters.
    /// Lets one composite route give each resource type its own query (e.g. Observation requires
    /// <c>category=laboratory</c> on Epic while Patient/Encounter need none). Null falls back to the route's value.
    /// </summary>
    public string? SearchParameters { get; private set; }

    /// <summary>
    /// Other resource mappings in the same route that this one is a "child" of. A mapping can have more than
    /// one — e.g. Observation can be a child of both Patient (via <c>subject</c>) and Encounter (via
    /// <c>encounter</c>) at once. Each link independently requires its own FHIR reference field to be mapped.
    /// </summary>
    public IReadOnlyCollection<ParentReferenceLink> ParentReferences => _parentReferences;

    public void ReplaceParentReferences(IEnumerable<ParentReferenceLink> parentReferences)
    {
        _parentReferences.Clear();
        _parentReferences.AddRange(
            parentReferences
                .GroupBy(x => x.ParentMappingProfileId)
                .Select(g => g.Last()));
    }
}

/// <summary>
/// Declares that a <see cref="ResourcePipelineRouteMapping"/> is a "child" of another mapping
/// (<see cref="ParentMappingProfileId"/>) in the same route — either the route's primary mapping or another
/// entry in <see cref="ResourcePipelineRoute.ResourceMappings"/>. This is the input to
/// <c>IParentReferenceResolver</c>, which determines which FHIR reference field the child must map as a
/// result (e.g. <c>Observation.subject</c> when the parent is Patient).
/// </summary>
public sealed class ParentReferenceLink
{
    private ParentReferenceLink()
    {
    }

    public ParentReferenceLink(Guid parentMappingProfileId, string? referenceFieldOverride = null)
    {
        Id = Guid.NewGuid();
        ParentMappingProfileId = parentMappingProfileId;
        ReferenceFieldOverride = referenceFieldOverride;
    }

    public Guid Id { get; private set; }
    public Guid ResourcePipelineRouteMappingId { get; private set; }
    public Guid ParentMappingProfileId { get; private set; }

    /// <summary>
    /// Escape hatch: the exact FHIR path (e.g. <c>"performer.actor.reference"</c>) to require instead of the
    /// field <c>IParentReferenceResolver</c> would auto-resolve. Only needed when a resource has more than one
    /// reference field that could target the same parent resource type and the auto-picked one is wrong for
    /// this mapping. Null means "use the auto-resolved field".
    /// </summary>
    public string? ReferenceFieldOverride { get; private set; }
}
