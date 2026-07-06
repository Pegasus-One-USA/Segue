namespace FHIRBridge.Application.Security;

/// <summary>
/// Canonical, code-side definition of the built-in RBAC reference data (permissions, system roles, and their
/// role→permission grants). This is the single source of truth consumed by the runtime RBAC bootstrapper that
/// self-provisions a freshly-migrated database on boot. It is the exact data that previously lived in the EF
/// <c>HasData</c> migration seed — same GUIDs, names, descriptions, categories, and mappings — moved from a
/// migration-time seed to an idempotent runtime bootstrap.
/// </summary>
public static class RbacSeedData
{
    /// <summary>
    /// A built-in permission row. Id/Name/DisplayName are all derived from Group+Action, never hand-picked —
    /// a new permission (a new Action under an existing or new Group) never needs a manually-minted GUID, and
    /// the same Action reused under a different Group (e.g. View under both User and Role) naturally gets a
    /// distinct Id since it's derived from the full Group+Action pair, not the Action alone.
    /// </summary>
    public sealed record PermissionSeed(string Description, PermissionGroupCode Group, PermissionActionCode Action)
    {
        public Guid Id
        {
            get { return PermissionTaxonomy.BuildPermissionId(Group, Action); }
        }

        public string Name
        {
            get { return PermissionTaxonomy.BuildPermissionCode(Group, Action); }
        }

        public string DisplayName
        {
            get { return PermissionTaxonomy.BuildPermissionDisplayName(Group, Action); }
        }
    }

    /// <summary>A built-in role row.</summary>
    public sealed record RoleSeed(Guid Id, string Name, string Description);

    /// <summary>
    /// A built-in permission category row (top tier, above groups). Id/Name/DisplayName are all derived from
    /// the enum member's <see cref="PermissionCategoryAttribute"/>, so the database can never drift from what
    /// <see cref="PermissionCategoryCode"/> declares.
    /// </summary>
    public sealed record CategorySeed(PermissionCategoryCode Category)
    {
        public Guid Id
        {
            get { return Category.GetId(); }
        }

        public string Name
        {
            get { return Category.ToString(); }
        }

        public string DisplayName
        {
            get { return Category.GetDisplayName(); }
        }
    }

    /// <summary>
    /// A built-in permission group row (middle tier, between categories and permissions). Id/Name/DisplayName
    /// are all derived from the enum member's <see cref="PermissionGroupAttribute"/>, so the database can
    /// never drift from what <see cref="PermissionGroupCode"/> declares.
    /// </summary>
    public sealed record GroupSeed(PermissionGroupCode Group)
    {
        public Guid Id
        {
            get { return Group.GetId(); }
        }

        public string Name
        {
            get { return Group.ToString(); }
        }

        public string DisplayName
        {
            get { return Group.GetDisplayName(); }
        }
    }

    /// <summary>
    /// The distinct top-level permission categories — every <see cref="PermissionCategoryCode"/> member, in
    /// declaration order. Adding a new category is purely an enum-file change; it appears here automatically.
    /// </summary>
    public static IReadOnlyList<CategorySeed> Categories { get; } =
        Enum.GetValues<PermissionCategoryCode>().Select(c => new CategorySeed(c)).ToArray();

    /// <summary>Looks up a category's seeded id by its <see cref="PermissionCategoryCode"/>.</summary>
    public static IReadOnlyDictionary<PermissionCategoryCode, Guid> CategoryIdsByCode { get; } =
        Categories.ToDictionary(c => c.Category, c => c.Id);

    /// <summary>
    /// The distinct permission groups — every <see cref="PermissionGroupCode"/> member, in declaration order.
    /// Adding a new group is purely an enum-file change; it appears here automatically.
    /// </summary>
    public static IReadOnlyList<GroupSeed> Groups { get; } =
        Enum.GetValues<PermissionGroupCode>().Select(g => new GroupSeed(g)).ToArray();

    /// <summary>Looks up a group's seeded id by its <see cref="PermissionGroupCode"/> (used to resolve <see cref="PermissionSeed.Group"/>).</summary>
    public static IReadOnlyDictionary<PermissionGroupCode, Guid> GroupIdsByCode { get; } =
        Groups.ToDictionary(g => g.Group, g => g.Id);

    /// <summary>
    /// The 20 built-in platform permissions, in seed order. Group/Action/description are preserved verbatim
    /// from the former <c>PermissionConfiguration.HasData</c> block (Group+Action replace the former flat Category).
    /// </summary>
    public static IReadOnlyList<PermissionSeed> Permissions { get; } =
    [
        // Original platform permissions.
        new("Manage source, destination, mapping, webhook, and route configuration.", PermissionGroupCode.Configuration, PermissionActionCode.Write),
        new("Execute configured pipeline routes.", PermissionGroupCode.Pipeline, PermissionActionCode.Execute),
        new("Read operational audit logs.", PermissionGroupCode.AuditLogs, PermissionActionCode.Read),
        new("Test source system connectivity.", PermissionGroupCode.SourceConnections, PermissionActionCode.Test),

        // User module permissions.
        new("Invite a new user to the organization.", PermissionGroupCode.User, PermissionActionCode.Invite),
        new("View the list of users.", PermissionGroupCode.User, PermissionActionCode.View),
        new("Update a user's profile information.", PermissionGroupCode.User, PermissionActionCode.Edit),
        new("Deactivate a user account.", PermissionGroupCode.User, PermissionActionCode.Deactivate),

        // Role module permissions.
        new("Create a new custom role.", PermissionGroupCode.Role, PermissionActionCode.Create),
        new("Edit an existing role.", PermissionGroupCode.Role, PermissionActionCode.Edit),
        new("Delete a custom role.", PermissionGroupCode.Role, PermissionActionCode.Delete),
        new("Assign or remove roles from users.", PermissionGroupCode.Role, PermissionActionCode.Assign),
        new("View roles and their permissions.", PermissionGroupCode.Role, PermissionActionCode.View),

        // Workflow module permissions.
        new("Create a new workflow.", PermissionGroupCode.Workflow, PermissionActionCode.Create),
        new("Edit an existing workflow.", PermissionGroupCode.Workflow, PermissionActionCode.Edit),
        new("Delete a workflow.", PermissionGroupCode.Workflow, PermissionActionCode.Delete),
        new("Execute a workflow.", PermissionGroupCode.Workflow, PermissionActionCode.Run),
        new("View workflow details.", PermissionGroupCode.Workflow, PermissionActionCode.View),

        // Report / payload permissions.
        new("View reports and analytics.", PermissionGroupCode.Report, PermissionActionCode.View),
        new("View data payloads from workflow runs.", PermissionGroupCode.Payload, PermissionActionCode.View),
    ];

    /// <summary>
    /// Looks up a built-in permission's seeded id by its (Group, Action) pair — the type-safe, typo-proof way
    /// to reference a specific permission (e.g. from <see cref="SystemRoleDefaultPermissions"/>) without needing
    /// a hand-maintained string constant for every permission.
    /// </summary>
    public static IReadOnlyDictionary<(PermissionGroupCode Group, PermissionActionCode Action), Guid> PermissionIdsByGroupAction { get; } =
        Permissions.ToDictionary(p => (p.Group, p.Action), p => p.Id);

    /// <summary>
    /// The 4 built-in system roles, in seed order. Descriptions are preserved verbatim from the former
    /// <c>RoleConfiguration.HasData</c> block.
    /// </summary>
    public static IReadOnlyList<RoleSeed> Roles { get; } =
    [
        new(SeededSecurityIds.SuperAdminRoleId, UnifiedRoles.SuperAdmin, "Full platform administrator."),
        new(SeededSecurityIds.AdminRoleId, UnifiedRoles.Admin, "Administers configuration and users."),
        new(SeededSecurityIds.OperationsRoleId, UnifiedRoles.Operations, "Builds and runs pipeline configurations, and reviews data and audit output."),
        new(SeededSecurityIds.AuditRoleId, UnifiedRoles.Audit, "Read-only access to configuration and audit logs."),
    ];

    /// <summary>
    /// Built-in role id → granted permission ids. Delegates to <see cref="SystemRoleDefaultPermissions.Grants"/>
    /// so the runtime bootstrap and the in-memory repository never drift.
    /// </summary>
    public static IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> RolePermissions
    {
        get { return SystemRoleDefaultPermissions.Grants; }
    }
}
