using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// A point-in-time snapshot of what a source FHIR endpoint (e.g. Epic) exposes, discovered from its
/// CapabilityStatement (<c>/metadata</c>). Persisted per source connection and refreshed on demand.
/// Used to gate which FHIR resource types a mapping may bind to: a mapping's resource type must be supported
/// by its source here, otherwise the mapping is rejected at save time before any pipeline can fail at runtime.
/// This is a standalone entity (deliberately NOT part of the <c>Tenant</c> aggregate) so discovery can refresh
/// it independently of configuration edits and without aggregate contention.
/// </summary>
public sealed class SourceCapabilityProfile : AuditableChildEntity<Guid>
{
    private SourceCapabilityProfile()
    {
    }

    public SourceCapabilityProfile(
        Guid tenantId,
        Guid sourceConnectionId,
        string fhirVersion,
        IReadOnlyList<CapabilityResource> resources,
        string[] configuredScopes,
        string? rawCapabilityJson,
        DateTime discoveredOnUtc)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        SourceConnectionId = sourceConnectionId;
        Replace(fhirVersion, resources, configuredScopes, rawCapabilityJson, discoveredOnUtc);
    }

    public Guid TenantId { get; private set; }
    public Guid SourceConnectionId { get; private set; }
    public string FhirVersion { get; private set; } = default!;

    /// <summary>Resource types the source reported as supported, with their interaction codes.</summary>
    public IReadOnlyList<CapabilityResource> Resources { get; private set; } = [];

    /// <summary>
    /// Scopes configured on the source connection at discovery time. Stored for display/audit; the authoritative
    /// "what this app was actually granted" refinement (the <c>scope</c> returned by the token endpoint) is a
    /// follow-up — gating today is driven by the CapabilityStatement.
    /// </summary>
    public string[] ConfiguredScopes { get; private set; } = [];

    /// <summary>The raw CapabilityStatement JSON, retained for audit and future element-level analysis.</summary>
    public string? RawCapabilityJson { get; private set; }

    public DateTime DiscoveredOnUtc { get; private set; }

    public void Replace(
        string fhirVersion,
        IReadOnlyList<CapabilityResource> resources,
        string[] configuredScopes,
        string? rawCapabilityJson,
        DateTime discoveredOnUtc)
    {
        FhirVersion = fhirVersion;
        Resources = resources;
        ConfiguredScopes = configuredScopes;
        RawCapabilityJson = rawCapabilityJson;
        DiscoveredOnUtc = discoveredOnUtc;
    }

    /// <summary>
    /// True when this source exposes <paramref name="resourceType"/> with a read or search interaction. Resource
    /// types absent from the discovered CapabilityStatement are treated as unsupported (returns false).
    /// </summary>
    public bool SupportsResourceType(string resourceType)
    {
        return Resources.Any(resource =>
            string.Equals(resource.ResourceType, resourceType, StringComparison.OrdinalIgnoreCase) &&
            resource.Interactions.Any(interaction =>
                interaction is "read" or "search-type" or "search"));
    }
}
