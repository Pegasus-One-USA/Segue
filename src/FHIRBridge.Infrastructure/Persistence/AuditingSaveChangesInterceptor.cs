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

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is IAppendOnlyEntity && entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    $"{entry.Metadata.ClrType.Name} is append-only and cannot be modified or deleted.");
            }

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

        // Owned-type dependents (e.g. DestinationConfiguration.SecretReference) are inline columns on the same
        // table as their owner, but EF tracks them as their own entries and cascades them to Deleted alongside
        // the owner. The owner flip above doesn't touch them, so without this they'd stay Deleted while the
        // owner becomes Modified — which makes EF write NULLs into the owned type's columns instead of
        // preserving their current values. Re-enumerate (rather than one pass) so this sees the owner state
        // changes just made above.
        //
        // A Deleted owned entry also shows up for a second, unrelated reason: reassigning an owned reference to a
        // brand-new instance (e.g. DestinationConfiguration.Update() doing `SecretReference = new SecretReference(...)`
        // for an otherwise-Modified, not-deleted owner) makes EF mark the old instance Deleted and track the new one
        // as Added, both under the same shared owner key — EF's normal handling collapses that pair into a single
        // UPDATE on its own. Blindly flipping every Deleted owned entry to Modified (as above) instead leaves BOTH
        // the old (now Modified) and new (Added) instances tracked under that same key, which throws
        // InvalidOperationException: "already being tracked". Skip the flip whenever a same-key Added replacement
        // already exists, so only the genuine owner-cascade case (no replacement) gets corrected.
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Deleted || !entry.Metadata.IsOwned())
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
                entry.State = EntityState.Modified;
            }
        }
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
            var entityId = entry.Property("Id").CurrentValue?.ToString();
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
                newValueJson: SerializeValues(entry, useOriginalValues: false),
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

    /// <summary>AuditLogConfiguration caps OldValueJson/NewValueJson at this length — never emit more.</summary>
    private const int MaxValueJsonLength = 4000;

    /// <summary>Per-property cap so one oversized column (e.g. a raw FHIR CapabilityStatement blob) can't by
    /// itself blow the whole snapshot past <see cref="MaxValueJsonLength"/>.</summary>
    private const int MaxPropertyValueLength = 200;

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

            snapshot[property.Metadata.Name] = ToAuditValue(values[property.Metadata]);
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
    private static object? ToAuditValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string text)
        {
            return Truncate(text);
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            return enumerable.Cast<object?>().Select(item => Truncate(item?.ToString())).ToList();
        }

        return Truncate(value.ToString());
    }

    private static string? Truncate(string? value)
    {
        if (value is null || value.Length <= MaxPropertyValueLength)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, MaxPropertyValueLength), $"…(truncated, {value.Length} total chars)");
    }
}
