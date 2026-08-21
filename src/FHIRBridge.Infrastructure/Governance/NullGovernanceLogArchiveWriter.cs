using FHIRBridge.Application.Abstractions.Governance;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>No-op archive writer for the in-memory (no-database) dev configuration — there is no store to archive from or into.</summary>
public sealed class NullGovernanceLogArchiveWriter : IGovernanceLogArchiveWriter
{
    public Task ArchiveAsync<TEntity>(
        string dataClass, IReadOnlyList<TEntity> rows, DateTime cutoffUtc, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
