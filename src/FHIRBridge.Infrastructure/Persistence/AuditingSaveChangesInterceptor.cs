using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.SharedKernel.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

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

        // Pass 1: never physically delete a soft-deletable root — flip it to a soft delete so audit/lineage
        // references stay resolvable.
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Deleted && entry.Entity is ISoftDeletable softDeletable)
            {
                entry.State = EntityState.Modified;
                softDeletable.ApplyDeleted(actor, now);
            }
        }

        // Pass 2: Remove() cascades EntityState.Deleted onto an aggregate's owned dependents too (e.g.
        // DestinationConfiguration.SecretReference, MappingProfile.Fields). Re-enumerate (rather than folding
        // into pass 1) so this sees the owner state changes pass 1 just made.
        //
        // Whether a Deleted owned entry should stay Deleted depends on WHY it's Deleted:
        //  - Genuine cascade from the OWNER's soft delete (pass 1 above) — the owned data must survive
        //    untouched (a single-instance owned type shares its owner's table/row, so EF would otherwise fold
        //    the "delete" into the same UPDATE and null out its — possibly required — columns).
        //  - An entirely ordinary update that reassigns a single-instance owned type to a new instance under
        //    the SAME shared key (e.g. `SecretReference = new SecretReference(...)`) — EF already collapses
        //    that Deleted+Added pair into one UPDATE on its own; do nothing.
        //  - An entirely ordinary update that replaces the contents of an OwnsMany collection (e.g.
        //    MappingProfile.Fields) — each item has its OWN independently-generated shadow key, so old and new
        //    items never share a key even though nothing is being soft-deleted. This is a genuine row removal
        //    and must stay Deleted, or the old row silently survives alongside its replacement (duplicate rows).
        //
        // The first case is the only one where the flip belongs — gated on the OWNER itself actually being
        // soft-deleted (resolved via the ownership foreign key), not merely on "this entry is Deleted".
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Deleted || !entry.Metadata.IsOwned())
            {
                continue;
            }

            var ownership = entry.Metadata.FindOwnership();
            if (ownership is null || !IsOwnerBeingSoftDeleted(context, entry, ownership))
            {
                continue;
            }

            var keyProperties = entry.Metadata.FindPrimaryKey()!.Properties;
            var keyValues = keyProperties.Select(entry.Property).Select(p => p.CurrentValue).ToArray();

            var hasReplacement = context.ChangeTracker.Entries()
                .Any(other => other.State == EntityState.Added
                    && other.Metadata == entry.Metadata
                    && keyProperties.Select(other.Property).Select(p => p.CurrentValue).SequenceEqual(keyValues));

            if (!hasReplacement)
            {
                entry.State = EntityState.Unchanged;
            }
        }

        // Pass 3: audit stamping, based on each entry's final state above.
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is not IAuditableEntity auditable)
            {
                continue;
            }

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

    /// <summary>
    /// Resolves an owned entry's owner via its ownership foreign key (matching FK values on the owned side
    /// against the principal key on the owner side) and reports whether that owner was itself just converted
    /// from a physical Deleted to a soft-deleted Modified above.
    /// </summary>
    private static bool IsOwnerBeingSoftDeleted(DbContext context, EntityEntry ownedEntry, IForeignKey ownership)
    {
        var fkValues = ownership.Properties.Select(ownedEntry.Property).Select(p => p.CurrentValue).ToArray();
        var principalKeyProperties = ownership.PrincipalKey.Properties;

        var ownerEntry = context.ChangeTracker.Entries().FirstOrDefault(other =>
            other.Metadata == ownership.PrincipalEntityType
            && principalKeyProperties.Select(other.Property).Select(p => p.CurrentValue).SequenceEqual(fkValues));

        return ownerEntry is { State: EntityState.Modified, Entity: ISoftDeletable { IsDeleted: true } };
    }
}
