namespace FHIRBridge.Application.Security;

public static class SeededSecurityIds
{
    // ── Platform roles (preserved from original seed) ────────────────────────
    public static readonly Guid SuperAdminRoleId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid AdminRoleId = Guid.Parse("10000000-0000-0000-0000-000000000002");
    public static readonly Guid OperationsRoleId = Guid.Parse("10000000-0000-0000-0000-000000000003");
    public static readonly Guid AuditRoleId = Guid.Parse("10000000-0000-0000-0000-000000000005");

    // Category, group, and permission ids are no longer hand-picked here. A category's/group's id lives
    // directly on its enum member via [PermissionCategory]/[PermissionGroup] (see PermissionCategoryCode.cs /
    // PermissionGroupCode.cs) — adding a new one is a single-file change. A permission's id is derived
    // deterministically from its Group+Action pair (see PermissionTaxonomy.BuildPermissionId), so the same
    // action reused under a different group (e.g. View under both User and Role) naturally gets a distinct
    // id instead of colliding.
}
