using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Messaging;

namespace FHIRBridge.Infrastructure.Messaging;

/// <summary>
/// In-process idempotency store for local development and tests. Production uses a durable store (Phase 2)
/// and/or Service Bus duplicate detection (Phase 4).
/// </summary>
public sealed class InMemoryProcessedMessageStore : IProcessedMessageStore
{
    private readonly ConcurrentDictionary<string, byte> _processed = new(StringComparer.Ordinal);

    public Task<bool> TryMarkProcessedAsync(string messageId, CancellationToken cancellationToken)
    {
        return Task.FromResult(_processed.TryAdd(messageId, 0));
    }
}
