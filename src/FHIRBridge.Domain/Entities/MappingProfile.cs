using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

public sealed class MappingProfile : AuditableChildEntity<Guid>, IHasAuditDisplayName
{
    private readonly List<MappingField> _fields = [];

    private MappingProfile()
    {
    }

    public MappingProfile(
        string name,
        string resourceType,
        Guid sourceConnectionId,
        Guid destinationId,
        string destinationObject,
        IEnumerable<MappingField> fields,
        Guid? sourceConfigurationId = null,
        string? mappingJson = null,
        Guid? workflowId = null)
    {
        Id = Guid.NewGuid();
        Name = name;
        ResourceType = resourceType;
        SourceConnectionId = sourceConnectionId;
        SourceConfigurationId = sourceConfigurationId;
        DestinationId = destinationId;
        DestinationObject = destinationObject;
        IsEnabled = true;
        MappingJson = mappingJson;
        WorkflowId = workflowId;
        ReplaceFields(fields);
    }

    public string Name { get; private set; } = default!;
    string? IHasAuditDisplayName.AuditDisplayName => Name;
    public string ResourceType { get; private set; } = default!;

    /// <summary>
    /// The source connection this mapping ingests from. A mapping is the single source of truth for source,
    /// destination, and resource type — routes that reference this mapping inherit all three.
    /// </summary>
    public Guid SourceConnectionId { get; private set; }

    /// <summary>
    /// The workflow-specific <see cref="SourceConfiguration"/> this mapping uses (search criteria, scopes, sync
    /// cursor) — additive alongside <see cref="SourceConnectionId"/> while the source-connection/configuration split
    /// (docs/backend/13-source-connection-configuration-split-plan.md) is rolled out. Nullable until Slice 2 cuts
    /// application code over to reading/writing it; populated by the Slice 1 migration backfill.
    /// </summary>
    public Guid? SourceConfigurationId { get; private set; }

    public Guid DestinationId { get; private set; }
    public string DestinationObject { get; private set; } = default!;
    public bool IsEnabled { get; private set; }
    public IReadOnlyCollection<MappingField> Fields => _fields.AsReadOnly();

    /// <summary>
    /// The workflow this profile belongs to — deliberately a plain, unconstrained Guid rather than an EF
    /// navigation to Runtime.Domain's WorkflowDefinition: that type lives in a separate layering track
    /// (Runtime.Domain ← Runtime.Application ← Runtime.Infrastructure per CLAUDE.md), and this project must not
    /// depend on it. Null on any profile created before this concept existed, or by a workflow-agnostic caller
    /// (the Mapping Config Import wizard, the standalone Mapping Profiles screen) — those keep matching on
    /// (ResourceType, SourceConnectionId, DestinationId) alone, same as always. Once two DIFFERENT workflows
    /// share that same triple, each gets its own profile instead of silently overwriting the other's mapping on
    /// every save — see ClaimForWorkflow and IConfigurationRepository.FindMappingProfileAsync.
    /// </summary>
    public Guid? WorkflowId { get; private set; }

    /// <summary>
    /// The complete, unmodified source JSON this profile was imported from (see the mapping-config import
    /// endpoint) — the source of truth for re-running the ETL later. <see cref="Fields"/> is a queryable
    /// projection of it, not a replacement.
    /// </summary>
    public string? MappingJson { get; private set; }

    /// <summary>
    /// Adopts an unclaimed (WorkflowId null) profile for the workflow that's re-saving it right now — the
    /// backward-compatible on-ramp for every profile that existed before WorkflowId did. A no-op once already
    /// claimed (by this same workflow or, in principle, another — the caller's own lookup already guarantees
    /// it only ever calls this on a profile that's unclaimed or already its own, so a different existing claim
    /// is left untouched rather than stolen).
    /// </summary>
    public void ClaimForWorkflow(Guid workflowId)
    {
        WorkflowId ??= workflowId;
    }

    public void Update(
        string name,
        string resourceType,
        Guid sourceConnectionId,
        Guid destinationId,
        string destinationObject,
        IEnumerable<MappingField> fields,
        Guid? sourceConfigurationId = null)
    {
        Name = name;
        ResourceType = resourceType;
        SourceConnectionId = sourceConnectionId;
        SourceConfigurationId = sourceConfigurationId;
        DestinationId = destinationId;
        DestinationObject = destinationObject;
        ReplaceFields(fields);
    }

    public void SetEnabled(bool isEnabled)
    {
        IsEnabled = isEnabled;
    }

    /// <summary>Replaces the raw source JSON this profile was (re-)imported from. Left untouched by
    /// <see cref="Update"/> so plain field edits via the configuration CRUD UI don't wipe it.</summary>
    public void SetMappingJson(string? mappingJson)
    {
        MappingJson = mappingJson;
    }

    private void ReplaceFields(IEnumerable<MappingField> fields)
    {
        _fields.Clear();
        // One row per destination column: a caller resubmitting the same TargetField more than once in a single
        // request (e.g. the field was re-picked in the mapping editor without the earlier row being cleared) must
        // not persist as separate rows. Keep the last occurrence — it reflects whatever the caller most recently
        // configured for that column.
        _fields.AddRange(
            fields
                .GroupBy(f => f.TargetField, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Last()));
    }
}
