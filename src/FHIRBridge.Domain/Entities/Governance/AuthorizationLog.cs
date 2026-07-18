using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Governance;

/// <summary>Immutable record of an RBAC authorization decision — written on denial (the compliance-relevant case).</summary>
public sealed class AuthorizationLog : Entity<Guid>, IAppendOnlyEntity
{
    private AuthorizationLog()
    {
    }

    public AuthorizationLog(
        Guid id,
        DateTime occurredOnUtc,
        string? userEmail,
        string requestPath,
        string permissionCode,
        string result,
        string? ipAddress,
        string? correlationId)
    {
        Id = id;
        OccurredOnUtc = occurredOnUtc;
        UserEmail = userEmail;
        RequestPath = requestPath;
        PermissionCode = permissionCode;
        Result = result;
        IpAddress = ipAddress;
        CorrelationId = correlationId;
    }

    public DateTime OccurredOnUtc { get; private set; }
    public string? UserEmail { get; private set; }
    public string RequestPath { get; private set; } = default!;
    public string PermissionCode { get; private set; } = default!;
    public string Result { get; private set; } = default!;
    public string? IpAddress { get; private set; }
    public string? CorrelationId { get; private set; }
}
