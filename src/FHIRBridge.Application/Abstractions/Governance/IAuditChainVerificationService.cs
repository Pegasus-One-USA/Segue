namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>Verifies the <c>AuditLogs</c> hash chain — shared by the Compliance Report (on-demand) and the
/// scheduled verification job (periodic), so the walk logic exists in exactly one place.</summary>
public interface IAuditChainVerificationService
{
    Task<ChainVerificationResult> VerifyAsync(CancellationToken cancellationToken);
}

public sealed record ChainVerificationResult(bool IsValid, int TotalEntries, long? FirstBrokenSequenceNumber);
