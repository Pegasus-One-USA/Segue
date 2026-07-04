namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// Idempotently provisions the built-in RBAC reference data (the permission catalog, the four system roles, and
/// their role→permission grants) into the database at startup. Replaces the former migration <c>HasData</c> seed:
/// a freshly-migrated, empty database self-provisions this data on boot. Creates NO users.
/// </summary>
public interface IRbacBootstrapper
{
    /// <summary>
    /// Inserts any missing PermissionCategory / Permission / Role / role-level PermissionAllocation rows
    /// (matched by Id). Existing rows are left untouched, so it is safe to run on every boot and picks up
    /// categories/permissions/roles/grants added since the last run.
    /// </summary>
    Task EnsureAsync(CancellationToken cancellationToken);
}
