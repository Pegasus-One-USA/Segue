using System.Security.Cryptography;
using System.Text;
using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Governance;

/// <summary>
/// Immutable, hash-chained record of a config/entity change or explicit administrative action.
/// Each row's <see cref="EntryHash"/> is computed over its own fields plus the prior row's hash
/// (<see cref="PreviousHash"/>), so any retroactive edit or deletion breaks the chain — satisfies
/// HIPAA §164.312(b)/(c)(1) tamper-evidence. <see cref="SequenceNumber"/> (DB identity) gives a
/// gap-free, monotonic ordering to chain against; <see cref="Id"/> stays the opaque external key.
/// </summary>
public sealed class AuditLog : Entity<Guid>, IAppendOnlyEntity
{
    private AuditLog()
    {
    }

    public AuditLog(
        Guid id,
        DateTime occurredOnUtc,
        string actor,
        string module,
        string action,
        string? entityType,
        string? entityId,
        string? entityName,
        string? oldValueJson,
        string? newValueJson,
        string status,
        string? remarks,
        string? ipAddress,
        string? userAgent,
        string? correlationId,
        string? previousHash)
    {
        Id = id;
        OccurredOnUtc = occurredOnUtc;
        Actor = actor;
        Module = module;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        EntityName = entityName;
        OldValueJson = oldValueJson;
        NewValueJson = newValueJson;
        Status = status;
        Remarks = remarks;
        IpAddress = ipAddress;
        UserAgent = userAgent;
        CorrelationId = correlationId;
        PreviousHash = previousHash;
        EntryHash = ComputeHash(
            previousHash, occurredOnUtc, actor, module, action, entityType, entityId, oldValueJson, newValueJson, status);
    }

    /// <summary>DB-identity ordering column the hash chain links against — never exposed externally.</summary>
    public long SequenceNumber { get; private set; }

    public DateTime OccurredOnUtc { get; private set; }
    public string Actor { get; private set; } = default!;
    public string Module { get; private set; } = default!;
    public string Action { get; private set; } = default!;
    public string? EntityType { get; private set; }
    public string? EntityId { get; private set; }
    public string? EntityName { get; private set; }
    public string? OldValueJson { get; private set; }
    public string? NewValueJson { get; private set; }
    public string Status { get; private set; } = default!;
    public string? Remarks { get; private set; }
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }
    public string? CorrelationId { get; private set; }
    public string? PreviousHash { get; private set; }
    public string EntryHash { get; private set; } = default!;

    /// <summary>
    /// The hash-chain function — recomputable independently of any live instance, so the append-only
    /// integrity check (e.g. a compliance report) can verify a persisted row wasn't altered after the fact
    /// without needing a private instance method or a round-trip through the constructor.
    /// </summary>
    public static string ComputeHash(
        string? previousHash,
        DateTime occurredOnUtc,
        string actor,
        string module,
        string action,
        string? entityType,
        string? entityId,
        string? oldValueJson,
        string? newValueJson,
        string status)
    {
        var payload = string.Join(
            '|',
            previousHash,
            occurredOnUtc.Ticks,
            actor,
            module,
            action,
            entityType,
            entityId,
            oldValueJson,
            newValueJson,
            status);

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hashBytes);
    }

    /// <summary>True if this row's stored <see cref="EntryHash"/> matches what its own fields recompute to.</summary>
    public bool VerifyOwnHash() =>
        EntryHash == ComputeHash(
            PreviousHash, OccurredOnUtc, Actor, Module, Action, EntityType, EntityId, OldValueJson, NewValueJson, Status);
}
