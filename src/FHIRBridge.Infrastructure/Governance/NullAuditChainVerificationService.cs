using FHIRBridge.Application.Abstractions.Governance;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>In-memory (no-database) dev configuration: there is no audit chain to verify.</summary>
public sealed class NullAuditChainVerificationService : IAuditChainVerificationService
{
    public Task<ChainVerificationResult> VerifyAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ChainVerificationResult(true, 0, null));
}
