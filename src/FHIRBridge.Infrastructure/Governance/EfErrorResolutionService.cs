using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities.Governance;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>EF-backed resolution triage. Upserts one mutable <see cref="ErrorResolution"/> row per error
/// reference; the underlying <see cref="ErrorLog"/> is never touched.</summary>
public sealed class EfErrorResolutionService : IErrorResolutionService
{
    private readonly FHIRBridgeDbContext _dbContext;
    private readonly ICurrentUserService _currentUserService;

    public EfErrorResolutionService(FHIRBridgeDbContext dbContext, ICurrentUserService currentUserService)
    {
        _dbContext = dbContext;
        _currentUserService = currentUserService;
    }

    public async Task<bool> ResolveAsync(
        string errorReferenceId, string resolvedBy, string? notes, CancellationToken cancellationToken)
    {
        var exists = await _dbContext.ErrorLogs
            .AsNoTracking()
            .AnyAsync(x => x.ErrorReferenceId == errorReferenceId, cancellationToken);
        if (!exists)
        {
            return false;
        }

        var resolution = await _dbContext.ErrorResolutions
            .FirstOrDefaultAsync(x => x.ErrorReferenceId == errorReferenceId, cancellationToken);
        if (resolution is null)
        {
            resolution = new ErrorResolution(Guid.NewGuid(), errorReferenceId);
            _dbContext.ErrorResolutions.Add(resolution);
        }

        var actor = string.IsNullOrWhiteSpace(resolvedBy) ? _currentUserService.CurrentUser.AuditName : resolvedBy;
        resolution.Resolve(actor, DateTime.UtcNow, notes);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ReopenAsync(string errorReferenceId, CancellationToken cancellationToken)
    {
        var resolution = await _dbContext.ErrorResolutions
            .FirstOrDefaultAsync(x => x.ErrorReferenceId == errorReferenceId, cancellationToken);
        if (resolution is null)
        {
            return false;
        }

        resolution.Reopen();
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
