using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>Walks the entire <c>AuditLogs</c> table verifying every row's own hash and its link to the
/// previous row. Used both on-demand (Compliance Report) and on a schedule (<c>AuditChainVerificationWorker</c>).</summary>
public sealed class EfAuditChainVerificationService : IAuditChainVerificationService
{
    private readonly FHIRBridgeDbContext _dbContext;

    public EfAuditChainVerificationService(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ChainVerificationResult> VerifyAsync(CancellationToken cancellationToken)
    {
        var rows = await _dbContext.AuditLogs.AsNoTracking()
            .OrderBy(x => x.SequenceNumber)
            .ToListAsync(cancellationToken);

        string? expectedPreviousHash = null;
        foreach (var row in rows)
        {
            if (!row.VerifyOwnHash() || row.PreviousHash != expectedPreviousHash)
            {
                return new ChainVerificationResult(false, rows.Count, row.SequenceNumber);
            }

            expectedPreviousHash = row.EntryHash;
        }

        return new ChainVerificationResult(true, rows.Count, null);
    }
}
