namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>
/// A transaction boundary spanning multiple <see cref="IConfigurationRepository"/> writes (source connections,
/// destinations, mapping profiles) plus anything else sharing the same underlying store within its lifetime —
/// e.g. <c>/api/v1/workflows/build</c>'s Destinations → Sources → Mappings → workflow-definition-save sequence,
/// which previously committed each step independently: a failure partway through (a validation error, a
/// mid-sequence exception) left earlier steps' entities durably persisted with no way to retry cleanly, since a
/// retry re-submits the same "create" requests and collides with what already landed (e.g. a source connection
/// name uniqueness violation on the second attempt). Only <see cref="CommitAsync"/> makes writes durable; disposing
/// without committing rolls everything back.
/// </summary>
public interface IConfigurationTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}
