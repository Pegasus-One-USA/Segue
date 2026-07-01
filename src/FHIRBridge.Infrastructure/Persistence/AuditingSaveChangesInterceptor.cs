using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.SharedKernel.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>
/// Stamps creation/modification provenance on <see cref="IAuditableEntity"/> rows and converts physical
/// deletes of <see cref="ISoftDeletable"/> rows into soft deletes, using the current user as the actor.
/// </summary>
public sealed class AuditingSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly ICurrentUserService _currentUserService;

    public AuditingSaveChangesInterceptor(ICurrentUserService currentUserService)
    {
        _currentUserService = currentUserService;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var actor = _currentUserService.CurrentUser.AuditName;
        var now = DateTime.UtcNow;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Deleted && entry.Entity is ISoftDeletable softDeletable)
            {
                // Never physically delete: flip to a soft delete so audit/lineage references stay resolvable.
                entry.State = EntityState.Modified;
                softDeletable.ApplyDeleted(actor, now);
            }

            if (entry.Entity is IAuditableEntity auditable)
            {
                switch (entry.State)
                {
                    case EntityState.Added:
                        auditable.ApplyCreated(actor, now);
                        break;
                    case EntityState.Modified:
                        auditable.ApplyModified(actor, now);
                        break;
                }
            }
        }
    }
}
