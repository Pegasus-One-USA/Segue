using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

public sealed class MappingProfile : AuditableChildEntity<Guid>
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
        string? mappingJson = null)
    {
        Id = Guid.NewGuid();
        Name = name;
        ResourceType = resourceType;
        SourceConnectionId = sourceConnectionId;
        DestinationId = destinationId;
        DestinationObject = destinationObject;
        IsEnabled = true;
        MappingJson = mappingJson;
        ReplaceFields(fields);
    }

    public string Name { get; private set; } = default!;
    public string ResourceType { get; private set; } = default!;

    /// <summary>
    /// The source connection this mapping ingests from. A mapping is the single source of truth for source,
    /// destination, and resource type — routes that reference this mapping inherit all three.
    /// </summary>
    public Guid SourceConnectionId { get; private set; }
    public Guid DestinationId { get; private set; }
    public string DestinationObject { get; private set; } = default!;
    public bool IsEnabled { get; private set; }
    public IReadOnlyCollection<MappingField> Fields => _fields.AsReadOnly();

    /// <summary>
    /// The complete, unmodified source JSON this profile was imported from (see the mapping-config import
    /// endpoint) — the source of truth for re-running the ETL later. <see cref="Fields"/> is a queryable
    /// projection of it, not a replacement.
    /// </summary>
    public string? MappingJson { get; private set; }

    public void Update(
        string name,
        string resourceType,
        Guid sourceConnectionId,
        Guid destinationId,
        string destinationObject,
        IEnumerable<MappingField> fields)
    {
        Name = name;
        ResourceType = resourceType;
        SourceConnectionId = sourceConnectionId;
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
        _fields.AddRange(fields);
    }
}
