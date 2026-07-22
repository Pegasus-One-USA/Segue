using FHIRBridge.Application.Abstractions.Governance;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>No-DB fallback: there is nothing to resolve when governance events aren't persisted.</summary>
public sealed class NullErrorResolutionService : IErrorResolutionService
{
    public Task<bool> ResolveAsync(string errorReferenceId, string resolvedBy, string? notes, CancellationToken cancellationToken)
        => Task.FromResult(false);

    public Task<bool> ReopenAsync(string errorReferenceId, CancellationToken cancellationToken)
        => Task.FromResult(false);
}
