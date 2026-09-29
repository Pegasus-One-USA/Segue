using System.Text.Json;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities.Governance;
using FHIRBridge.SharedKernel.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>
/// Stamps creation/modification provenance on <see cref="IAuditableEntity"/> rows, converts physical
/// deletes of <see cref="ISoftDeletable"/> rows into soft deletes, blocks modification of
/// <see cref="IAppendOnlyEntity"/> rows, and appends one <see cref="AuditLog"/> row per
/// <see cref="IAuditableEntity"/> change (config/entity Created/Updated/Deleted) to the same
/// SaveChanges batch — this is what makes every governed config change show up in the audit trail
/// without every call site having to remember to log it.
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

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        await AppendConfigAuditLogsAsync(eventData.Context, cancellationToken);
        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var actor = _currentUserService.CurrentUser.AuditName;
        var now = DateTime.UtcNow;

        // Pass 0 (PostgreSQL only): RowVersion has no store-generated value on this provider — see
        // FHIRBridgeDbContext.OnModelCreating's Npgsql branch — so stamp a fresh value on every
        // Added/Modified entry ourselves. This is what actually makes the column non-null on insert and
        // makes a stale concurrent update's WHERE clause miss (triggering the same DbUpdateConcurrencyException
        // SQL Server's native rowversion produces automatically).
        if (context.Database.IsNpgsql())
        {
            foreach (var entry in context.ChangeTracker.Entries())
            {
                if (entry.State is EntityState.Added or EntityState.Modified
                    && entry.Properties.FirstOrDefault(p => p.Metadata.Name == "RowVersion") is { } rowVersion)
                {
                    rowVersion.CurrentValue = Guid.NewGuid().ToByteArray();
                }
            }
        }

        // Pass 1: never physically delete a soft-deletable root — flip it to a soft delete so audit/lineage
        // references stay resolvable.
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is IAppendOnlyEntity && entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    $"{entry.Metadata.ClrType.Name} is append-only and cannot be modified or deleted.");
            }

            if (entry.State == EntityState.Deleted && entry.Entity is ISoftDeletable softDeletable)
            {
                entry.State = EntityState.Modified;
                softDeletable.ApplyDeleted(actor, now);
            }
            // A Deleted owned entry (e.g. SourceConnection.Authentication, or its own nested PrivateKey/
            // ClientSecret) is fixed up below, in the second pass over Entries() — NOT here. This used to also
            // unconditionally flip any Deleted+owned entry to Unchanged right here, which silently broke every
            // "reassign an owned reference to a brand-new instance" update (e.g. ConfigurationMapper.ToDomain
            // building a fresh SecretReference/SourceAuthenticationConfiguration on every save): flipping the
            // orphaned old instance to Unchanged before the second pass ever saw it as Deleted pre-empted that
            // pass's own (correctly guarded) handling, so the new instance's Added entry never turned into a real
            // UPDATE — the old (stale) values silently won, with no error and no visible sign anything was wrong.
            // See the second pass's own remarks for the full soft-delete-cascade vs. reference-replacement story.
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

    /// <summary>
    /// Appends one <see cref="AuditLog"/> row per <see cref="IAuditableEntity"/> Added/Modified/soft-Deleted
    /// entry in this same SaveChanges batch, chained onto the last persisted row's hash. Async-only: every
    /// call site in this codebase already uses SaveChangesAsync, so the sync <see cref="Stamp"/> path
    /// intentionally doesn't duplicate this (a DB round-trip for the previous hash has no sync-safe story
    /// here without blocking).
    /// </summary>
    private async Task AppendConfigAuditLogsAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (context is null)
        {
            return;
        }

        var pending = new List<(EntityEntry Entry, string Action)>();
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is not IAuditableEntity)
            {
                continue;
            }

            var action = entry.State switch
            {
                EntityState.Added => "Created",
                EntityState.Modified when entry.Entity is ISoftDeletable { IsDeleted: true } => "Deleted",
                EntityState.Modified => "Updated",
                // A physical delete. Only reachable for an auditable entity that is NOT ISoftDeletable — pass 1
                // above converts every soft-deletable Remove() into the Modified case handled just above, so this
                // never double-counts one. UserRole is the motivating case: revoking a role really does remove the
                // row, and without this branch the revocation would leave no trace whatsoever. Note the snapshot
                // must be taken from ORIGINAL values here; the row is about to cease to exist.
                EntityState.Deleted => "Deleted",
                _ => null,
            };

            if (action is not null)
            {
                pending.Add((entry, action));
            }
        }

        if (pending.Count == 0)
        {
            return;
        }

        var current = _currentUserService.CurrentUser;
        var previousHash = await context.Set<AuditLog>()
            .OrderByDescending(x => x.SequenceNumber)
            .Select(x => x.EntryHash)
            .FirstOrDefaultAsync(cancellationToken);

        foreach (var (entry, action) in pending)
        {
            var entityId = ResolveEntityId(entry);
            var entityType = entry.Metadata.ClrType.Name;
            var entityName = entry.Entity is IHasAuditDisplayName named ? named.AuditDisplayName : null;

            var auditLog = new AuditLog(
                Guid.NewGuid(),
                DateTime.UtcNow,
                current.AuditName,
                entityType,
                action,
                entityType,
                entityId,
                entityName,
                oldValueJson: action == "Updated" || action == "Deleted" ? SerializeValues(entry, useOriginalValues: true) : null,
                // A physically-deleted row has no meaningful "after" state — and asking EF for CurrentValues on a
                // Deleted entry is not safe to rely on. The row's last known state is carried by oldValueJson above,
                // which for a physical delete is the only surviving record of what was removed.
                newValueJson: entry.State == EntityState.Deleted
                    ? null
                    : SerializeValues(entry, useOriginalValues: false),
                status: "Success",
                remarks: null,
                current.IpAddress,
                current.UserAgent,
                current.CorrelationId,
                previousHash);

            context.Set<AuditLog>().Add(auditLog);
            previousHash = auditLog.EntryHash;
        }
    }

    /// <summary>
    /// The audited entity's key, as a string for <c>AuditLog.EntityId</c>.
    /// <para>Resolved from the entity's real primary key rather than a property literally named "Id", because not
    /// every auditable entity has one: <c>UserRole</c> is keyed on (<c>UserId</c>, <c>RoleId</c>) and has no <c>Id</c>
    /// property at all. Asking for one by name throws here — inside <c>SavingChangesAsync</c>, where it would fail
    /// the entire save (the real entity change included), not merely the audit row.</para>
    /// <para>A single-key entity yields exactly what the old <c>Property("Id")</c> lookup did, so existing rows keep
    /// their shape and remain matchable by the <c>(EntityType, EntityId)</c> index. A composite key is joined with
    /// '|' in the key's own declared order, which EF keeps stable.</para>
    /// </summary>
    private static string? ResolveEntityId(EntityEntry entry)
    {
        var primaryKey = entry.Metadata.FindPrimaryKey();
        if (primaryKey is null)
        {
            return null;
        }

        var values = primaryKey.Properties
            .Select(property => entry.Property(property.Name).CurrentValue?.ToString());

        return string.Join("|", values);
    }

    /// <summary>
    /// Overall snapshot cap. The column itself is now unbounded (see AuditLogConfiguration), so this is a sanity
    /// bound on one entity's whole snapshot rather than a hard storage limit.
    /// </summary>
    private const int MaxValueJsonLength = 64_000;

    /// <summary>Per-property cap so one oversized column (e.g. a raw FHIR CapabilityStatement blob) can't by
    /// itself blow the whole snapshot past <see cref="MaxValueJsonLength"/>.</summary>
    private const int MaxPropertyValueLength = 200;

    /// <summary>
    /// Properties that carry the substance of a config change rather than incidental metadata, and so are exempt
    /// from <see cref="MaxPropertyValueLength"/>. <c>WorkflowNode.ConfigurationJson</c> holds the field mapping,
    /// transform rules and destination settings — clipping it at 200 chars would leave an audit row that shows the
    /// opening brace of a mapping and nothing that changed. Bounded by <see cref="MaxValueJsonLength"/> instead.
    /// </summary>
    private static readonly HashSet<string> UncappedProperties = new(StringComparer.Ordinal)
    {
        "ConfigurationJson",
    };

    /// <summary>
    /// Properties whose value is replaced with a placeholder in the audit snapshot. The audit trail records
    /// <em>that</em> a secret was provisioned or rotated, by whom and when — never the material itself.
    /// <para><c>AuditLog</c> is append-only and retained for years, so anything landing here is effectively
    /// permanent: copying even a DataProtection-encrypted value into it widens the blast radius of that key and
    /// undoes <c>ProvisionedSecret</c>'s own "only the protected value is persisted" guarantee. The change is
    /// still fully auditable — a rotation shows old and new as redacted, with the actor and timestamp intact.</para>
    /// </summary>
    private static readonly HashSet<string> RedactedProperties = new(StringComparer.Ordinal)
    {
        "ProtectedValue",
    };

    private const string RedactedPlaceholder = "***redacted***";

    /// <summary>
    /// Flattens an entry's own scalar properties to JSON for the audit trail's old/new value columns.
    /// Skips <c>RowVersion</c> (opaque concurrency token, not a meaningful diff) and any binary column.
    /// </summary>
    private static string SerializeValues(EntityEntry entry, bool useOriginalValues)
    {
        var values = useOriginalValues ? entry.OriginalValues : entry.CurrentValues;
        var snapshot = new Dictionary<string, object?>();

        foreach (var property in entry.Properties)
        {
            if (property.Metadata.ClrType == typeof(byte[]) || property.Metadata.Name == "RowVersion")
            {
                continue;
            }

            if (RedactedProperties.Contains(property.Metadata.Name))
            {
                // Placeholder only when there was actually a value, so "was unset, now set" stays visible.
                snapshot[property.Metadata.Name] = values[property.Metadata] is null ? null : RedactedPlaceholder;
                continue;
            }

            snapshot[property.Metadata.Name] = ToAuditValue(
                values[property.Metadata],
                uncapped: UncappedProperties.Contains(property.Metadata.Name));
        }

        var json = JsonSerializer.Serialize(snapshot);
        if (json.Length <= MaxValueJsonLength)
        {
            return json;
        }

        // Defensive fallback: per-property truncation above should already keep this under the column limit for
        // any realistic entity, but if an entity has enough properties to still overflow, fall back to a small,
        // always-valid JSON object rather than let a 4000-char SQL column truncation throw and fail the whole
        // SaveChanges batch (this audit row shares a transaction with the real entity change being audited).
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["_truncated"] = $"Snapshot was {json.Length} chars, exceeding the {MaxValueJsonLength}-char audit column limit, and was omitted.",
        });
    }

    /// <summary>
    /// Converts a tracked property's current/original value into something that serializes meaningfully in the
    /// audit snapshot: collections (e.g. <c>string[]</c>) are kept as real arrays instead of falling through to
    /// <see cref="object.ToString"/> (which for an array just returns its CLR type name, e.g. "System.String[]"),
    /// and any string form is capped at <see cref="MaxPropertyValueLength"/> so a single large text/JSON column
    /// can't dominate the snapshot's total size.
    /// </summary>
    private static object? ToAuditValue(object? value, bool uncapped = false)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string text)
        {
            return Truncate(text, uncapped);
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            return enumerable.Cast<object?>().Select(item => Truncate(item?.ToString(), uncapped)).ToList();
        }

        return Truncate(value.ToString(), uncapped);
    }

    private static string? Truncate(string? value, bool uncapped = false)
    {
        if (value is null || uncapped || value.Length <= MaxPropertyValueLength)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, MaxPropertyValueLength), $"…(truncated, {value.Length} total chars)");
    }
}
