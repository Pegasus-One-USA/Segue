# 06 — Adding Permissions (How-To)

> **Audience:** developers adding a new permission-gated feature.
> **Scope:** the mechanical steps only. For *why* the system works this way, see [05 — Permission Auto-Generation](05-permission-auto-generation.md).

## The one rule

You never hand-pick a permission's Id, wire-format code, or display name. You only ever declare **which `Group` and `Action` an endpoint needs** — everything else is generated. If the `Group`/`Action`/`Category` you need doesn't exist yet, add it first (below), then use it.

## Adding a new Category

Edit **one file**: `src/FHIRBridge.Application/Rbac/PermissionCategoryCode.cs`.

```csharp
public enum PermissionCategoryCode
{
    [PermissionCategory("40000000-0000-0000-0000-000000000001", "Access Control")]
    AccessControl = 1,

    [PermissionCategory("40000000-0000-0000-0000-000000000002", "Platform")]
    Platform = 2,

    [PermissionCategory("40000000-0000-0000-0000-000000000003", "Pipelines")]
    Pipelines = 3,

    // Add your new category here:
    [PermissionCategory("40000000-0000-0000-0000-000000000004", "Reporting")]
    Reporting = 4,
}
```

- Pick the next unused integer value.
- Pick a **new, never-before-used** Guid for the first argument — mint any fresh Guid (e.g. `[Guid]::NewGuid()` in PowerShell, or any online generator); it just has to be stable forever afterward. This project's built-in categories happen to follow a `40000000-...-000N` pattern for readability, but that's a convention, not a requirement — any valid, unique Guid works.
- The second argument is the human-readable display name shown in the permission-management UI.

Nothing else to touch — `RbacSeedData.Categories` is built from `Enum.GetValues<PermissionCategoryCode>()`, so the new category appears automatically on the next boot.

## Adding a new Group

Edit **one file**: `src/FHIRBridge.Application/Rbac/PermissionGroupCode.cs`.

```csharp
public enum PermissionGroupCode
{
    [PermissionGroup("30000000-0000-0000-0000-000000000004", PermissionCategoryCode.AccessControl, "User")]
    User = 1,

    // ...existing groups...

    // Add your new group here:
    [PermissionGroup("30000000-0000-0000-0000-000000000012", PermissionCategoryCode.Reporting, "Dashboard")]
    Dashboard = 12,
}
```

- Same rules as a category: next unused int, a fresh unique Guid.
- The second argument is the `PermissionCategoryCode` that owns this group — must already exist (add the category first if needed).
- Third argument is the display name.

If you ever need to move a group to a different category later, just change the `PermissionCategoryCode` argument in this one attribute — `RbacBootstrapper` re-syncs the row's `CategoryId` on the next boot, no migration, no data loss.

## Adding a new Action

Edit **one file**: `src/FHIRBridge.Application/Rbac/PermissionActionCode.cs`.

```csharp
public enum PermissionActionCode
{
    [PermissionDisplayName("View")]
    View = 1,

    // ...existing actions...

    // Add your new action here:
    [PermissionDisplayName("Export")]
    Export = 13,
}
```

- Next unused int, and the display text used when building a permission's display name (e.g. `"Export Dashboard"`).
- Actions don't carry their own Guid — they're never a standalone row, only ever combined with a `Group` to derive a permission's Id.

**Before reusing an existing action for a new group**, check whether it already means what you want — `View`/`Create`/`Edit`/`Delete` are meant to be reused across groups (that's the point: `user.view` and `role.view` are two different permissions, same `Action`). Only add a brand-new action if none of the existing ones fit.

## Adding the `[StandardPermission]` attribute to an endpoint

On a controller class (guards every action in the controller) or a specific action method:

```csharp
[HttpGet]
[StandardPermission(PermissionGroupCode.Dashboard, PermissionActionCode.Export, description: "Export the operations dashboard to PDF.")]
public async Task<IActionResult> ExportDashboard(CancellationToken cancellationToken)
{
    ...
}
```

- First two arguments: the `Group` and `Action` this endpoint requires. `Category` is **not** an argument — it's derived from the group automatically (see doc 05 for why: passing it separately was removed specifically because it let a group/category mismatch compile and crash at runtime).
- `description` is optional but should always be supplied for a new permission — if you skip it, the permission is still created and the endpoint still works, but a startup warning is logged (`"Auto-registered new permission ... with no description"`) reminding you to add one.

That's it. On the next application boot:
- `PermissionCatalog` discovers the attribute via reflection.
- `Program.cs`'s `SyncDiscoveredPermissionsAsync` creates the `Permission` row (`IsSystem = false`) if it doesn't already exist, and grants it to SuperAdmin automatically.
- An authorization policy (`HasPermission:{group}.{action}`) is registered automatically — nothing to add to `Program.cs`'s policy-registration loop, it already iterates every discovered/declared code.

## Adding a permission whose required group depends on runtime data (resource-based / dynamic)

Use this instead of `[StandardPermission]` when the endpoint's required `Group` can't be known until the request is inspected — the running example is `ConfigurationsController.AddSourceConnection`, where the permission depends on which EHR vendor (`SourceSystemType`) the request is for.

**If the vendor already has no dedicated group** (i.e. it currently falls back to the generic `SourceConnections` permission), promoting it to its own dedicated, auto-discovered permission is **one line**:

```csharp
// PermissionGroupCode.cs — the ONLY change needed. The member name must match the
// SourceSystemType member name exactly (case-sensitive) — that's the whole mechanism.
[PermissionGroup("30000000-0000-0000-0000-000000000013", PermissionCategoryCode.Pipelines, "Allscripts")]
Allscripts = 13,
```

Nothing else — no attribute change, no seed-data edit, no controller change. On the next boot, `SourceSystemPermissionGroups.GroupFor(SourceSystemType.Allscripts)` resolves to `PermissionGroupCode.Allscripts` by name, `PermissionCatalog`'s dynamic pass crosses it with the `Edit` action already declared on `ConfigurationsController.AddSourceConnection`, and `allscripts.edit` appears as a real, grantable permission. This was proven end to end this session — see `tests/FHIRBridge.Api.IntegrationTests/Tests/SourceConnectionPermissionTests.cs`.

**If you're wiring up a brand-new resource-based endpoint from scratch** (not just adding a vendor to an existing one):

```csharp
[HttpPost("source-connections")]
[DynamicSourceSystemPermission(typeof(SourceSystemType), PermissionActionCode.Edit, description: "Add or edit a source connection.")]
public async Task<IActionResult> AddSourceConnection([FromBody] CreateSourceConnectionRequest request, CancellationToken cancellationToken)
{
    var denied = await this.AuthorizePermissionAsync(_authorizationService, request.SourceSystemType, PermissionActionCode.Edit);
    if (denied is not null) return denied;

    // ... normal action body ...
}
```

1. `[DynamicSourceSystemPermission(typeof(TEnum), action, description)]` — declares which enum type to loop over and which action to cross it with. This is metadata only; it does not enforce anything by itself.
2. `this.AuthorizePermissionAsync(_authorizationService, <the actual enum value from the request>, action)` — the real check, called explicitly in the action body. It resolves the group from the value the exact same way the attribute above tells `PermissionCatalog` to at startup, so the two can never disagree about which permissions exist versus which get enforced.
3. A vendor/type value with **no** same-named `PermissionGroupCode` member falls back to the generic `PermissionGroupCode.SourceConnections` group automatically — no error, just shared with every other unmapped value. If you want it to have its own dedicated permission instead, add the one-line enum member shown above.

**Don't do this** if the endpoint's permission is fixed and known at compile time — that's what `[StandardPermission]` is for. Reach for the dynamic mechanism only when the group genuinely can't be decided until a request arrives.

## Reusing the same permission on more than one endpoint

Just apply the same `[StandardPermission(group, action)]` pair on as many controllers/methods as need it — this is expected and fully supported:

```csharp
// UsersController.cs
[StandardPermission(PermissionGroupCode.Report, PermissionActionCode.View, description: "View reports and analytics.")]
public async Task<IActionResult> GetUserReports(...) { ... }

// ReportsController.cs
[StandardPermission(PermissionGroupCode.Report, PermissionActionCode.View, description: "View reports and analytics.")]
public async Task<IActionResult> GetAllReports(...) { ... }
```

Both resolve to the exact same `report.view` permission (same Id, one database row). If the two descriptions differ, both get kept (joined with `" | "`) rather than one silently overwriting the other. The `Instances` column on the `Permission` row records every declaring `ClassName.MethodName`, so you can always see everywhere a permission is actually enforced.

## Should this also go in `RbacSeedData.Permissions`?

Usually, **no** — leave it as a discovered-only permission (`IsSystem = false`). Only add an entry to `src/FHIRBridge.Application/Rbac/RbacSeedData.cs`'s `Permissions` list if you specifically want it treated as a **built-in** permission:

| | Discovered-only (default — do nothing extra) | Seed-declared (add to `RbacSeedData.Permissions`) |
|---|---|---|
| `IsSystem` | `false` | `true` |
| Granted to SuperAdmin automatically | Yes | Only if you also add it to `SystemRoleDefaultPermissions` |
| Removable by just deleting the `[StandardPermission]` usage | Yes — deactivates automatically | No — deactivating a seed-declared permission means removing it from `RbacSeedData.Permissions`, not just removing attribute usages |
| Typical use | Any new feature-gated endpoint | Core platform permissions you want to explicitly curate (e.g. deciding which of the 4 built-in roles get it by default) |

If you do add one to `RbacSeedData.Permissions`, also decide whether `SuperAdmin`/`Admin`/`Operations`/`Audit` should be granted it by default — `SuperAdmin`/`Admin` get every seed-declared permission automatically (`SystemRoleDefaultPermissions.AllPermissions`); `Operations`/`Audit` need an explicit entry in `SystemRoleDefaultPermissions.Grants`.

## Checklist for a new permission-gated feature

1. Does the `Group` you need already exist? If not, add it (and its `Category`, if that's also new).
2. Does the `Action` you need already exist and mean what you want? If not, add it.
3. Is the required group fixed and known at compile time? Apply `[StandardPermission(group, action, description: "...")]`. Does it instead depend on data inside the request? Use the dynamic mechanism above.
4. Build and boot once (locally) to confirm the permission gets created — check the `Permissions` table for the new row, or check `/api/v1/permissions/catalog`.
5. If this should be a built-in, curated permission (not just "exists because an endpoint needs it"), add it to `RbacSeedData.Permissions` and decide which built-in roles get it.
6. Double-check your new int value and Guid (if any) aren't copy-pasted from an existing member. `RbacDefinitionValidator` catches a duplicate at boot and logs an error instead of writing corrupted data, but it's still better to not rely on that — pick a genuinely fresh Guid and the next unused int, every time.
