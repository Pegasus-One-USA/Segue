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
            else if (entry.State == EntityState.Deleted && entry.Metadata.IsOwned())
            {
                // Remove() cascades EntityState.Deleted onto an aggregate's owned sub-entities (e.g.
                // SourceConnection.Authentication) too. Converting only the root to a soft-delete Modified above
                // leaves those owned entries still Deleted — and since an owned type sharing its owner's table has
                // no separate row to delete, EF folds that into the same UPDATE by nulling its columns, which
                // throws for any required (NOT NULL) owned property. Untracked-as-unchanged instead: a soft delete
                // must never touch the owned data at all, required or not.
                entry.State = EntityState.Unchanged;
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
