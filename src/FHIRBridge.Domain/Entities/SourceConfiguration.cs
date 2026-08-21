using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// The workflow-specific configuration of how a pipeline pulls data through a reusable <see cref="SourceConnection"/>:
/// search criteria, resource types, sync cursor, and the OAuth scopes that workflow needs. Introduced so that editing
/// retrieval settings for one workflow never forks a duplicate connection (see
/// docs/backend/13-source-connection-configuration-split-plan.md).
/// </summary>
public sealed class SourceConfiguration : AuditableChildEntity<Guid>, IHasAuditDisplayName
{
    private SourceConfiguration()
    {
    }

    public SourceConfiguration(
        Guid connectionId,
        string name,
        string[] scopes,
        SourceRetrievalConfiguration? retrieval = null)
    {
        Id = Guid.NewGuid();
        ConnectionId = connectionId;
        Name = name;
        Scopes = scopes ?? [];
        Retrieval = retrieval;
    }

    public Guid ConnectionId { get; private set; }
    public string Name { get; private set; } = default!;
    string? IHasAuditDisplayName.AuditDisplayName => Name;
    public string[] Scopes { get; private set; } = [];

    /// <summary>How this workflow polls for resources; null for interactive sources where retrieval is driven by the
    /// launch context rather than a configured poll.</summary>
    public SourceRetrievalConfiguration? Retrieval { get; private set; }

    public void Update(string name, string[] scopes, SourceRetrievalConfiguration? retrieval)
    {
        Name = name;
        Scopes = scopes ?? [];
        Retrieval = retrieval;
    }

    /// <summary>Advances the incremental-sync cursor for the given resource types after this workflow's run
    /// completes successfully. No-op when this configuration has no retrieval configuration.</summary>
    public void RecordRetrievalSync(IReadOnlyCollection<string> resourceTypes, DateTime syncedAtUtc)
    {
        if (Retrieval is not null)
        {
            Retrieval = Retrieval.WithLastSuccessfulSync(resourceTypes, syncedAtUtc);
        }
    }

    /// <summary>Replaces the requested OAuth scopes for this workflow, leaving retrieval settings untouched.</summary>
    public void UpdateScopes(string[] scopes)
    {
        Scopes = scopes ?? [];
    }
}
