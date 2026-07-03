using System.Security.Cryptography;
using System.Text;
using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// Append-only, PHI-free record of an end-user action (who did what, when, from where). Distinct from
/// <see cref="OperationalAuditLog"/>, which records pipeline/system events. Each row is sealed into a
/// hash chain (<see cref="PreviousHash"/> + <see cref="EntryHash"/>) so tampering with history
/// is detectable (HIPAA §164.312(b)/(c)).
/// </summary>
public sealed class UserActivityAuditLog : Entity<Guid>
{
    private UserActivityAuditLog()
    {
    }

    public UserActivityAuditLog(
        Guid? userId,
        string userEmail,
        string category,
        string activity,
        string status,
        string? entityName,
        Guid? entityId,
        string? ipAddress,
        string? userAgent,
        string? httpMethod,
        string? requestPath,
        string? details,
        string? correlationId,
        string? sessionId,
        string? failureReason,
        string severity,
        DateTime occurredOnUtc)
    {
        Id = Guid.NewGuid();
        UserId = userId;
        UserEmail = userEmail;
        Category = category;
        Activity = activity;
        Status = status;
        EntityName = entityName;
        EntityId = entityId;
        IpAddress = ipAddress;
        UserAgent = userAgent;
        HttpMethod = httpMethod;
        RequestPath = requestPath;
        Details = details;
        CorrelationId = correlationId;
        SessionId = sessionId;
        FailureReason = failureReason;
        Severity = severity;
        OccurredOnUtc = occurredOnUtc;
    }

    public Guid? UserId { get; private set; }
    public string UserEmail { get; private set; } = default!;
    public string Category { get; private set; } = default!;
    public string Activity { get; private set; } = default!;
    public string Status { get; private set; } = default!;
    public string? EntityName { get; private set; }
    public Guid? EntityId { get; private set; }
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }
    public string? HttpMethod { get; private set; }
    public string? RequestPath { get; private set; }
    public string? Details { get; private set; }
    public string? CorrelationId { get; private set; }
    public string? SessionId { get; private set; }
    public string? FailureReason { get; private set; }
    public string Severity { get; private set; } = default!;
    public DateTime OccurredOnUtc { get; private set; }

    /// <summary>Hash of the previous entry in the chain (null for the first entry).</summary>
    public string? PreviousHash { get; private set; }

    /// <summary>SHA-256 of this row's canonical content plus <see cref="PreviousHash"/>.</summary>
    public string EntryHash { get; private set; } = default!;

    /// <summary>
    /// Links this entry into the tamper-evident chain by recording the prior entry's hash and computing
    /// this entry's hash over its immutable content. Call once, immediately before persisting.
    /// </summary>
    public void SealChain(string? previousHash)
    {
        PreviousHash = previousHash;
        EntryHash = ComputeHash(previousHash);
    }

    private string ComputeHash(string? previousHash)
    {
        var canonical = string.Join(
            '|',
            Id,
            UserId,
            UserEmail,
            Category,
            Activity,
            Status,
            EntityName,
            EntityId,
            IpAddress,
            UserAgent,
            HttpMethod,
            RequestPath,
            Details,
            CorrelationId,
            SessionId,
            FailureReason,
            Severity,
            OccurredOnUtc.ToString("O"),
            previousHash);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes);
    }
}
