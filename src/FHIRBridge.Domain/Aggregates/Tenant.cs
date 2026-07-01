using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.Fhir;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Abstractions;
using FHIRBridge.SharedKernel.Enums;

namespace FHIRBridge.Domain.Aggregates;

public sealed class Tenant : AuditableEntity<Guid>
{
    private readonly List<SourceConnection> _sourceConnections = [];
    private readonly List<WebhookConfiguration> _webhookConfigurations = [];
    private readonly List<DestinationConfiguration> _destinationConfigurations = [];
    private readonly List<MappingProfile> _mappingProfiles = [];
    private readonly List<ResourcePipelineRoute> _resourcePipelineRoutes = [];

    private Tenant()
    {
    }

    public Tenant(string name, string code)
    {
        Id = Guid.NewGuid();
        Name = name;
        Code = code;
        Status = TenantStatus.Active;
        IsEnabled = true;
    }

    public string Name { get; private set; } = default!;
    public string Code { get; private set; } = default!;
    public TenantStatus Status { get; private set; }

    /// <summary>Hard enable/disable switch. When false, login and all pipeline activity for the tenant is blocked.</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>HIPAA retention window (days) for purgeable operational data; null = platform default.</summary>
    public int? RetentionDays { get; private set; }

    /// <summary>IANA time zone used to interpret this tenant's cron schedules; null = UTC.</summary>
    public string? TimeZone { get; private set; }

    /// <summary>Primary administrative contact email for the tenant.</summary>
    public string? ContactEmail { get; private set; }

    /// <summary>Data-residency region hint for the tenant.</summary>
    public string? Region { get; private set; }

    public IReadOnlyCollection<SourceConnection> SourceConnections => _sourceConnections.AsReadOnly();
    public IReadOnlyCollection<WebhookConfiguration> WebhookConfigurations => _webhookConfigurations.AsReadOnly();
    public IReadOnlyCollection<DestinationConfiguration> DestinationConfigurations => _destinationConfigurations.AsReadOnly();
    public IReadOnlyCollection<MappingProfile> MappingProfiles => _mappingProfiles.AsReadOnly();

    /// <summary>
    /// Pipeline routes hang directly off the tenant. A route's FHIR resource type is resolved from its
    /// <see cref="ResourcePipelineRoute.MappingProfileId"/> (see <see cref="ResolveResourceType"/>) — there is no
    /// separate ResourceConfiguration entity.
    /// </summary>
    public IReadOnlyCollection<ResourcePipelineRoute> ResourcePipelineRoutes => _resourcePipelineRoutes.AsReadOnly();

    public void UpdateName(string name)
    {
        Name = name;
    }

    public void SetEnabled(bool isEnabled)
    {
        IsEnabled = isEnabled;
    }

    public void UpdateGovernance(int? retentionDays, string? timeZone, string? contactEmail, string? region)
    {
        RetentionDays = retentionDays;
        TimeZone = timeZone;
        ContactEmail = contactEmail;
        Region = region;
    }

    /// <summary>
    /// The single source of truth for a route's FHIR resource type: the resource type of its mapping profile.
    /// Returns null if the mapping can't be found (shouldn't happen for a validly-built route).
    /// </summary>
    public string? ResolveResourceType(ResourcePipelineRoute route)
    {
        return _mappingProfiles.FirstOrDefault(x => x.Id == route.MappingProfileId)?.ResourceType;
    }

    /// <summary>
    /// The single source of truth for a route's source connection: the source of its mapping profile.
    /// Returns null if the mapping can't be found (shouldn't happen for a validly-built route).
    /// </summary>
    public Guid? ResolveSourceConnectionId(ResourcePipelineRoute route)
    {
        return _mappingProfiles.FirstOrDefault(x => x.Id == route.MappingProfileId)?.SourceConnectionId;
    }

    /// <summary>
    /// The single source of truth for a route's destination: the destination of its mapping profile.
    /// Returns null if the mapping can't be found (shouldn't happen for a validly-built route).
    /// </summary>
    public Guid? ResolveDestinationId(ResourcePipelineRoute route)
    {
        return _mappingProfiles.FirstOrDefault(x => x.Id == route.MappingProfileId)?.DestinationId;
    }

    public SourceConnection AddSourceConnection(
        string name,
        SourceSystemType sourceSystemType,
        string baseUrl,
        SourceAuthenticationConfiguration authentication,
        ApplicationType? applicationType = null,
        SourceInteractiveConfiguration? interactive = null)
    {
        var sourceConnection = new SourceConnection(
            Id, name, sourceSystemType, baseUrl, authentication, applicationType, interactive);
        _sourceConnections.Add(sourceConnection);

        return sourceConnection;
    }

    public SourceConnection UpdateSourceConnection(
        Guid sourceConnectionId,
        string name,
        SourceSystemType sourceSystemType,
        string baseUrl,
        SourceAuthenticationConfiguration authentication,
        ApplicationType? applicationType = null,
        SourceInteractiveConfiguration? interactive = null)
    {
        var sourceConnection = _sourceConnections.FirstOrDefault(x => x.Id == sourceConnectionId);
        if (sourceConnection is null)
        {
            throw new InvalidOperationException("Source connection does not belong to this tenant.");
        }

        sourceConnection.Update(name, sourceSystemType, baseUrl, authentication, applicationType, interactive);

        return sourceConnection;
    }

    public SourceConnection SetSourceConnectionEnabled(Guid sourceConnectionId, bool isEnabled)
    {
        var sourceConnection = _sourceConnections.FirstOrDefault(x => x.Id == sourceConnectionId);
        if (sourceConnection is null)
        {
            throw new InvalidOperationException("Source connection does not belong to this tenant.");
        }

        sourceConnection.SetEnabled(isEnabled);

        if (!isEnabled)
        {
            foreach (var webhook in _webhookConfigurations.Where(x => x.SourceConnectionId == sourceConnectionId))
            {
                webhook.SetEnabled(false);
            }

            // A route's source is owned by its mapping, so cascade Source -> MappingProfiles -> their routes.
            var affectedMappingIds = _mappingProfiles
                .Where(x => x.SourceConnectionId == sourceConnectionId)
                .Select(x => x.Id)
                .ToHashSet();

            foreach (var mappingProfile in _mappingProfiles.Where(x => x.SourceConnectionId == sourceConnectionId))
            {
                mappingProfile.SetEnabled(false);
            }

            foreach (var route in _resourcePipelineRoutes.Where(x => affectedMappingIds.Contains(x.MappingProfileId)))
            {
                route.SetEnabled(false);
            }
        }

        return sourceConnection;
    }

    public WebhookConfiguration AddWebhookConfiguration(
        Guid sourceConnectionId,
        string resourceType,
        string name,
        string path,
        bool isEnabled)
    {
        EnsureSourceConnection(sourceConnectionId);
        var normalizedResourceType = SupportedFhirResourceTypes.Normalize(resourceType);

        var existing = _webhookConfigurations.FirstOrDefault(x =>
            x.SourceConnectionId == sourceConnectionId &&
            string.Equals(x.ResourceType, normalizedResourceType, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.Update(normalizedResourceType, name, path, isEnabled);

            return existing;
        }

        var webhookConfiguration = new WebhookConfiguration(Id, sourceConnectionId, normalizedResourceType, name, path, isEnabled);
        _webhookConfigurations.Add(webhookConfiguration);

        return webhookConfiguration;
    }

    public WebhookConfiguration SetWebhookConfigurationEnabled(Guid webhookConfigurationId, bool isEnabled)
    {
        var webhookConfiguration = _webhookConfigurations.FirstOrDefault(x => x.Id == webhookConfigurationId);
        if (webhookConfiguration is null)
        {
            throw new InvalidOperationException("Webhook configuration does not belong to this tenant.");
        }

        webhookConfiguration.SetEnabled(isEnabled);

        if (!isEnabled)
        {
            foreach (var route in _resourcePipelineRoutes.Where(x => x.WebhookConfigurationId == webhookConfigurationId))
            {
                route.SetEnabled(false);
            }
        }

        return webhookConfiguration;
    }

    public DestinationConfiguration AddDestinationConfiguration(
        string name,
        DestinationType destinationType,
        SecretReference secretReference,
        string? target)
    {
        var destinationConfiguration = new DestinationConfiguration(Id, name, destinationType, secretReference, target);
        _destinationConfigurations.Add(destinationConfiguration);

        return destinationConfiguration;
    }

    public DestinationConfiguration UpdateDestinationConfiguration(
        Guid destinationId,
        string name,
        DestinationType destinationType,
        SecretReference secretReference,
        string? target)
    {
        var destinationConfiguration = _destinationConfigurations.FirstOrDefault(x => x.Id == destinationId);
        if (destinationConfiguration is null)
        {
            throw new InvalidOperationException("Destination configuration does not belong to this tenant.");
        }

        destinationConfiguration.Update(name, destinationType, secretReference, target);

        return destinationConfiguration;
    }

    public DestinationConfiguration SetDestinationConfigurationEnabled(Guid destinationId, bool isEnabled)
    {
        var destinationConfiguration = _destinationConfigurations.FirstOrDefault(x => x.Id == destinationId);
        if (destinationConfiguration is null)
        {
            throw new InvalidOperationException("Destination configuration does not belong to this tenant.");
        }

        destinationConfiguration.SetEnabled(isEnabled);

        if (!isEnabled)
        {
            var affectedMappingIds = _mappingProfiles
                .Where(x => x.DestinationId == destinationId)
                .Select(x => x.Id)
                .ToHashSet();

            foreach (var mappingProfile in _mappingProfiles.Where(x => x.DestinationId == destinationId))
            {
                mappingProfile.SetEnabled(false);
            }

            // A route's destination is owned by its mapping, so disable routes whose mapping targets this destination.
            foreach (var route in _resourcePipelineRoutes.Where(x => affectedMappingIds.Contains(x.MappingProfileId)))
            {
                route.SetEnabled(false);
            }
        }

        return destinationConfiguration;
    }

    public MappingProfile AddMappingProfile(
        string name,
        string resourceType,
        Guid sourceConnectionId,
        Guid destinationId,
        string destinationObject,
        IEnumerable<MappingField> fields)
    {
        EnsureSourceConnection(sourceConnectionId);
        EnsureDestination(destinationId);
        var normalizedResourceType = SupportedFhirResourceTypes.Normalize(resourceType);

        var mappingProfile = new MappingProfile(
            Id,
            name,
            normalizedResourceType,
            sourceConnectionId,
            destinationId,
            destinationObject,
            fields);

        _mappingProfiles.Add(mappingProfile);

        return mappingProfile;
    }

    public MappingProfile UpdateMappingProfile(
        Guid mappingProfileId,
        string name,
        string resourceType,
        Guid sourceConnectionId,
        Guid destinationId,
        string destinationObject,
        IEnumerable<MappingField> fields)
    {
        EnsureSourceConnection(sourceConnectionId);
        EnsureDestination(destinationId);
        var normalizedResourceType = SupportedFhirResourceTypes.Normalize(resourceType);
        var mappingProfile = _mappingProfiles.FirstOrDefault(x => x.Id == mappingProfileId);
        if (mappingProfile is null)
        {
            throw new InvalidOperationException("Mapping profile does not belong to this tenant.");
        }

        var bindingChanged = mappingProfile.DestinationId != destinationId
            || mappingProfile.SourceConnectionId != sourceConnectionId;

        mappingProfile.Update(
            name,
            normalizedResourceType,
            sourceConnectionId,
            destinationId,
            destinationObject,
            fields);

        // A mapping owns its routes' resource type, source, and destination; if its source or destination changes,
        // dependent routes are disabled so a stale source→destination binding can't run until reviewed.
        if (bindingChanged)
        {
            foreach (var route in _resourcePipelineRoutes.Where(x => x.MappingProfileId == mappingProfileId))
            {
                route.SetEnabled(false);
            }
        }

        return mappingProfile;
    }

    public MappingProfile SetMappingProfileEnabled(Guid mappingProfileId, bool isEnabled)
    {
        var mappingProfile = _mappingProfiles.FirstOrDefault(x => x.Id == mappingProfileId);
        if (mappingProfile is null)
        {
            throw new InvalidOperationException("Mapping profile does not belong to this tenant.");
        }

        mappingProfile.SetEnabled(isEnabled);

        if (!isEnabled)
        {
            foreach (var route in _resourcePipelineRoutes.Where(x => x.MappingProfileId == mappingProfileId))
            {
                route.SetEnabled(false);
            }
        }

        return mappingProfile;
    }

    /// <summary>
    /// Adds (or updates, if an identical webhook+mapping route already exists) a pipeline route. The route's
    /// resource type, source, and destination are all taken from the mapping profile — none are passed in.
    /// </summary>
    public ResourcePipelineRoute AddRoute(
        IngestionMode ingestionMode,
        Guid? webhookConfigurationId,
        Guid mappingProfileId,
        string? scheduleExpression,
        string? searchParameters,
        bool isEnabled,
        int priority)
    {
        var mappingProfile = EnsureMappingProfile(mappingProfileId);

        if (webhookConfigurationId.HasValue)
        {
            EnsureWebhookConfiguration(webhookConfigurationId.Value, mappingProfile.SourceConnectionId, mappingProfile.ResourceType);
        }

        var existing = _resourcePipelineRoutes.FirstOrDefault(x =>
            x.WebhookConfigurationId == webhookConfigurationId &&
            x.MappingProfileId == mappingProfileId);

        if (existing is not null)
        {
            existing.Update(
                ingestionMode,
                webhookConfigurationId,
                mappingProfileId,
                scheduleExpression,
                searchParameters,
                isEnabled,
                priority);

            return existing;
        }

        var route = new ResourcePipelineRoute(
            Id,
            webhookConfigurationId,
            mappingProfileId,
            ingestionMode,
            scheduleExpression,
            searchParameters,
            isEnabled,
            priority);

        _resourcePipelineRoutes.Add(route);

        return route;
    }

    public ResourcePipelineRoute SetRouteEnabled(Guid routeId, bool isEnabled)
    {
        var route = _resourcePipelineRoutes.FirstOrDefault(x => x.Id == routeId)
            ?? throw new InvalidOperationException("Resource route does not belong to this tenant.");

        route.SetEnabled(isEnabled);

        return route;
    }

    /// <summary>
    /// Enables/disables every route whose mapping resolves to <paramref name="resourceType"/>. Replaces the old
    /// per-ResourceConfiguration toggle now that resource type is owned by the mapping.
    /// </summary>
    public IReadOnlyList<ResourcePipelineRoute> SetRoutesEnabledForResourceType(string resourceType, bool isEnabled)
    {
        var normalizedResourceType = SupportedFhirResourceTypes.Normalize(resourceType);
        var routes = _resourcePipelineRoutes
            .Where(route => string.Equals(ResolveResourceType(route), normalizedResourceType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var route in routes)
        {
            route.SetEnabled(isEnabled);
        }

        return routes;
    }

    public ResourcePipelineRoute UpdateRoute(
        Guid routeId,
        IngestionMode ingestionMode,
        Guid? webhookConfigurationId,
        Guid mappingProfileId,
        string? scheduleExpression,
        string? searchParameters,
        bool isEnabled,
        int priority)
    {
        var mappingProfile = EnsureMappingProfile(mappingProfileId);

        if (webhookConfigurationId.HasValue)
        {
            EnsureWebhookConfiguration(webhookConfigurationId.Value, mappingProfile.SourceConnectionId, mappingProfile.ResourceType);
        }

        var route = _resourcePipelineRoutes.FirstOrDefault(x => x.Id == routeId)
            ?? throw new InvalidOperationException("Resource route does not belong to this tenant.");

        var duplicate = _resourcePipelineRoutes.Any(x =>
            x.Id != routeId &&
            x.WebhookConfigurationId == webhookConfigurationId &&
            x.MappingProfileId == mappingProfileId);

        if (duplicate)
        {
            throw new InvalidOperationException("Another route already exists for the same webhook and mapping.");
        }

        route.Update(
            ingestionMode,
            webhookConfigurationId,
            mappingProfileId,
            scheduleExpression,
            searchParameters,
            isEnabled,
            priority);

        return route;
    }

    private void EnsureSourceConnection(Guid sourceConnectionId)
    {
        if (_sourceConnections.All(x => x.Id != sourceConnectionId))
        {
            throw new InvalidOperationException("Source connection does not belong to this tenant.");
        }
    }

    private void EnsureWebhookConfiguration(Guid webhookConfigurationId, Guid sourceConnectionId, string resourceType)
    {
        var webhookConfiguration = _webhookConfigurations.FirstOrDefault(x => x.Id == webhookConfigurationId);
        if (webhookConfiguration is null)
        {
            throw new InvalidOperationException("Webhook configuration does not belong to this tenant.");
        }

        if (webhookConfiguration.SourceConnectionId != sourceConnectionId)
        {
            throw new InvalidOperationException("Webhook configuration does not belong to the selected source connection.");
        }

        if (!string.Equals(webhookConfiguration.ResourceType, resourceType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Webhook configuration resource type does not match the route's mapping resource type.");
        }
    }

    private void EnsureDestination(Guid destinationId)
    {
        if (_destinationConfigurations.All(x => x.Id != destinationId))
        {
            throw new InvalidOperationException("Destination configuration does not belong to this tenant.");
        }
    }

    private MappingProfile EnsureMappingProfile(Guid mappingProfileId)
    {
        return _mappingProfiles.FirstOrDefault(x => x.Id == mappingProfileId)
            ?? throw new InvalidOperationException("Mapping profile does not belong to this tenant.");
    }
}
