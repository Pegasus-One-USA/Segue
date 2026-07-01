using System.Collections.Concurrent;
using FHIRBridge.Runtime.Application.Abstractions.Auth;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

/// <summary>
/// In-process, single-use store for in-flight interactive OAuth sign-ins. Suitable for a single host; a distributed
/// deployment would back this with the shared cache (as the token store does) so the callback can land on any node.
/// </summary>
public sealed class InMemoryOAuthAuthorizationStateStore : IOAuthAuthorizationStateStore
{
    private readonly ConcurrentDictionary<string, PendingAuthorization> _pending = new(StringComparer.Ordinal);

    public Task SaveAsync(string state, PendingAuthorization pending, CancellationToken cancellationToken)
    {
        _pending[state] = pending;
        return Task.CompletedTask;
    }

    public Task<PendingAuthorization?> TakeAsync(string state, CancellationToken cancellationToken)
    {
        _pending.TryRemove(state, out var pending);
        return Task.FromResult(pending);
    }
}
