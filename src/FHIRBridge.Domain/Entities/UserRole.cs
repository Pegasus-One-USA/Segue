using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// A global role assignment — the record of who was granted which role, and when it was taken away.
/// <para><see cref="IAuditableEntity"/> but deliberately NOT <see cref="ISoftDeletable"/>. Soft-deleting would
/// leave revoked assignments as live rows that every read has to remember to filter, and
/// <c>EfUserAccessRepository.GetUserRolesAsync</c> feeds <c>SuperAdminOnlyAuthorizationHandler</c> directly — a
/// revoked role must genuinely stop existing. Adding an audit trail must not change who can do what.</para>
/// <para>The revocation is still recorded: <c>AuditingSaveChangesInterceptor</c> has an explicit
/// <c>EntityState.Deleted</c> branch for auditable-but-not-soft-deletable entities, added for exactly this case,
/// which writes a "Deleted" row carrying the removed assignment in <c>OldValueJson</c>.</para>
/// </summary>
public sealed class UserRole : IAuditableEntity
{
    private UserRole()
    {
    }

    public UserRole(Guid userId, Guid roleId)
    {
        UserId = userId;
        RoleId = roleId;
        IsEnabled = true;
    }

    public Guid UserId { get; private set; }
    public Guid RoleId { get; private set; }

    /// <summary>Soft-disables a global role assignment without removing the row.</summary>
    public bool IsEnabled { get; private set; }

    public DateTime CreatedOnUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>Seeded for the same reason as <see cref="AuditTrackedEntity{TId}.CreatedBy"/>: mapped NOT NULL,
    /// and role assignments are also created by the RBAC bootstrapper, which runs with no current user.</summary>
    public string? CreatedBy { get; private set; } = "system";
    public DateTime? ModifiedOnUtc { get; private set; }
    public string? ModifiedBy { get; private set; }

    public void SetEnabled(bool isEnabled)
    {
        IsEnabled = isEnabled;
    }

    public void ApplyCreated(string? userId, DateTime utcNow)
    {
        CreatedOnUtc = utcNow;
        CreatedBy = userId;
    }

    public void ApplyModified(string? userId, DateTime utcNow)
    {
        ModifiedOnUtc = utcNow;
        ModifiedBy = userId;
    }
}
