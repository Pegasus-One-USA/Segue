# 05 — Permission Auto-Generation & Request Flow

> **Audience:** developers touching RBAC/permission code.
> **Scope:** how a permission's identity (Id, Name, DisplayName) is generated, how it gets into the database, how a live request is actually authorized against it end to end, and every file involved. See [06 — Adding Permissions](06-adding-permissions-howto.md) for the practical how-to.

## The problem this solves

Before this system, every permission needed a hand-picked GUID, a hand-typed wire-format code, and a hand-typed display name — three places to keep in sync, and nothing stopped a typo (`[StandardPermission("usr.viw")]`) from silently registering a policy nobody could ever satisfy.

Now: a permission's entire identity is a **pure function of two enum values** (`PermissionGroupCode`, `PermissionActionCode`). Given the same pair, you always get the same Id, the same wire-format code, and the same display name — in every environment, on every boot, with nobody hand-picking anything.

## The three pipelines

There are **three independent ways a permission comes to exist**, and they never touch the same row:

| | Built-in permissions | Discovered (static) | Discovered (dynamic / resource-based) |
|---|---|---|---|
| Declared in | `RbacSeedData.Permissions` (a C# list) | `[StandardPermission]` on a controller/action | `[DynamicSourceSystemPermission]` on an action, crossed with every value of a declared enum type |
| Use case | A fixed `(Group, Action)` known at compile time | Same | The required `Group` can only be known once the request body is inspected (e.g. which EHR vendor a source connection is for) |
| `IsSystem` | `true` | `false` | `false` |
| Owned/synced by | `RbacBootstrapper` | `Program.cs` → `SyncDiscoveredPermissionsAsync` | Same as static-discovered — `PermissionCatalog` produces both kinds side by side and `Program.cs` doesn't distinguish them for writing |
| Runs when | Every boot, before discovery sync | Every boot, after `RbacBootstrapper` | Same boot pass as static-discovered |
| Typical use | Permissions you've deliberately promoted into the canonical seed list | Any `[StandardPermission]` usage not in that list | A vendor/type axis where adding a new value (e.g. a new EHR vendor) should never require touching this code again |

A permission can start as **discovered-only**, and later be promoted into `RbacSeedData.Permissions` — at that point `SyncDiscoveredPermissionsAsync` stops touching it entirely (see "Ownership handoff" below) and `RbacBootstrapper` takes over for good.

## Boot-time flow

```
Program.cs (top level)
  builder.Build() → app
  │
  ├─ BootstrapDatabase(app)
  │    ├─ dbContext.Database.Migrate()                  (apply pending EF migrations)
  │    └─ IRbacBootstrapper.EnsureAsync()                → RbacBootstrapper.EnsureAsync (see below)
  │         │
  │         └─ RbacDefinitionValidator.Validate()        ← runs FIRST, before any write (see "Duplicate-definition
  │              (any duplicate → log + return, DB untouched)   validation" below)
  │
  └─ SyncDiscoveredPermissions(app)
       └─ SyncDiscoveredPermissionsAsync(repository, logger)   (see below)
```

Both run synchronously (`.GetAwaiter().GetResult()`) during startup, before `app.Run()` — a request is never served against a partially-synced permission table.

### `RbacBootstrapper.EnsureAsync` (built-in permissions)

File: `src/FHIRBridge.Infrastructure/Rbac/RbacBootstrapper.cs`

0. **Validate first** — calls `RbacDefinitionValidator.Validate()`. If it returns any errors, each is logged via `ILogger.LogError` and the method **returns immediately** — no category, group, or permission row is touched this boot. See "Duplicate-definition validation" below.
1. **Categories** — for each `RbacSeedData.Categories` entry, find the existing row by Id; if found, re-sync `Name` (if a `PermissionCategoryCode` member was renamed) and `DisplayName`; if not found, insert. Save.
2. **Groups** — same pattern, plus re-syncs `Name` and `CategoryId` if a group's owning category changed in code (`PermissionGroupAttribute`'s `category` argument). Save.
3. **Permissions** — for each `RbacSeedData.Permissions` entry:
   - Look up an existing row **by Id first, then by Name** (`existingPermissionsByName`, built by indexer assignment so a Name shared by more than one row — active or not — never throws). The Name fallback is what makes this self-healing: if the Id-derivation formula ever changes (it has, more than once, this project's history), an already-seeded permission is still recognized as "the same permission" and never duplicated.
   - If found: re-sync `Name` (a Group/Action enum member can be renamed — its Id, derived from the underlying int value, stays the same, but the stored wire-format code must be re-synced or it silently stops matching the freshly-computed authorization policy), `DisplayName`, `GroupId`; **merge** (never overwrite) `Description` if the seed's text isn't already a substring of what's stored; **reactivate** it if it was previously deactivated.
   - If not found: insert a new row with `IsSystem = true`.
   - Either way, record `seed.Id → actual row Id` in `actualPermissionIdBySeedId` — needed because the *seed's* theoretical Id and the *row's* real Id can differ after a Name-fallback match.
4. **Deactivate orphans** — any `IsSystem = true`, currently-active row whose Id wasn't matched by any `RbacSeedData.Permissions` entry this boot gets `Deactivate()`'d (never deleted — `PermissionAllocations` referencing it, i.e. role grants and audit history, stay intact).
5. **Roles** — insert any of the built-in roles (`RbacSeedData.Roles`) missing by Id.
6. **Role→Permission grants** — for each `RbacSeedData.RolePermissions` entry, translate the seed's permission Id through `actualPermissionIdBySeedId` (step 3) before checking/inserting the grant — this is what keeps a role's grants pointed at the right row even when a permission's Id drifted.

### `SyncDiscoveredPermissionsAsync` (discovered permissions — static and dynamic)

File: `src/Api/FHIRBridge.Api/Program.cs`

1. Call `PermissionCatalog.DiscoveredPermissions(assembly)` — reflects over every controller class/method for `[StandardPermission]` **and** `[DynamicSourceSystemPermission]` attributes (see "What discovery actually does" and "The dynamic / resource-based mechanism" below).
2. Build `seedDeclaredPermissionIds` — every Id already in `RbacSeedData.Permissions`. **Anything in this set is skipped entirely** by this method, both in the deactivation pass and the create/update pass — it's `RbacBootstrapper`'s territory, not this method's. This is the fix for a real bug: before this guard existed, this method would try to "self-heal" a built-in permission the same way it heals a discovered one, which meant deactivating the real, active, `IsSystem = true` row and inserting a *second*, `IsSystem = false` row under the same Name — corrupting the row's system-ness and causing a dictionary-duplicate-key crash the next time `RbacBootstrapper` ran.
3. **Deactivate orphans** — a currently-active, non-seed-declared permission not present in this boot's discovered set gets deactivated. (Re-declaring it later reactivates the same row — see step 4.)
4. **Update or create** — for each discovered permission (already deduplicated/combined by `PermissionCatalog`, see below):
   - Found by Id → re-sync `Name`/`DisplayName`/`Description`/`Instances`; reactivate if it was inactive.
   - Not found → insert a new `IsSystem = false` row, and grant it to the SuperAdmin role automatically. If it has no description, log a warning — this is meant to be caught in review, not silently accepted.

### What `PermissionCatalog.DiscoveredPermissions` actually does

File: `src/Api/FHIRBridge.Api/Rbac/PermissionCatalog.cs`

This method does the **combining**, not `Program.cs` — by the time `SyncDiscoveredPermissionsAsync` sees a `DiscoveredPermission`, duplicates are already merged, and static vs. dynamic origin is already resolved. It runs two independent passes over the same reflection scan:

**Static pass** (`[StandardPermission]`):
1. Reflect over every `ControllerBase`-derived type; collect every `[StandardPermission]` on the class itself and on each public instance method, each tagged with its declaring location (`"UsersController"` for a class-level attribute, `"UsersController.InviteUser"` for a method-level one).
2. Group all of these occurrences by their wire-format `PermissionCode` (case-insensitive) — i.e. by the same `(Group, Action)` pair.
3. For each group, produce **one** `DiscoveredPermission` (`IsDynamic = false`): `Description` = every attribute's non-empty description in that group, deduplicated and joined with `" | "`; `Instances` = every declaring location, deduplicated and joined with `", "`; `Group`/`Action`/`Code` come from the first occurrence.

**Dynamic pass** (`[DynamicSourceSystemPermission]`), see the dedicated section below for what problem this solves — summarized here for how it feeds into the same output list:
4. Group every `[DynamicSourceSystemPermission]` occurrence by `(EnumType, Action)`.
5. For each group, call `SourceSystemPermissionGroups.AllGroupsFor(enumType)` — every `PermissionGroupCode` a value of that enum can resolve to right now.
6. Cross each resolved group with the action to build a code (`PermissionTaxonomy.BuildPermissionCode`). If a `[StandardPermission]` already produced that exact code (step 3), skip it — a real static declaration always wins over an implied dynamic one. Otherwise add a `DiscoveredPermission` with `IsDynamic = true`, and a description with the resolved group's display name appended (e.g. `"Add or edit a source connection. (Epic)"`) so Epic/Cerner/Athenahealth/... don't all show identical text.

`DiscoveredPermission.IsDynamic` is what lets `FindUndeclaredCodes` (the typo-detection check logged at startup, see `Program.cs`) tell a genuine `[StandardPermission]` typo apart from a dynamically-discovered code that's *supposed* to have no seed entry — without it, every dynamically-discovered code would falsely warn as "used but not declared" on every single boot.

## The dynamic / resource-based mechanism

Some endpoints can't use a fixed `[StandardPermission(group, action)]` at all: the actual permission group they need depends on data inside the request, only known once it's inspected. The canonical example is `ConfigurationsController.AddSourceConnection` — a source connection's required permission depends on which EHR vendor (`SourceSystemType`) it's for, and Epic/Cerner/Athenahealth/Allscripts each need their own dedicated permission.

**The pieces:**

- **`SourceSystemPermissionGroups.GroupFor(Enum value)`** (`src/FHIRBridge.Application/Rbac/SourceSystemPermissionGroups.cs`) resolves the `PermissionGroupCode` for *any* enum value **by name, not a hand-maintained dictionary**: `Enum.TryParse<PermissionGroupCode>(value.ToString())`. A value with no same-named `PermissionGroupCode` member falls back to the generic `PermissionGroupCode.SourceConnections` group. Before parsing, it checks `Enum.IsDefined(value.GetType(), value)` on the *incoming* value — without that check, an out-of-range integer (allowed through by the default `JsonStringEnumConverter`, which permits raw integers) could coincidentally parse into an unrelated vendor's `PermissionGroupCode` by number, letting a user authorized for one vendor create a connection tagged as a different, undefined vendor.
- **`SourceSystemPermissionGroups.AllGroupsFor(Type enumType)`** — every group a value of `enumType` can resolve to right now, deliberately excluding the generic `SourceConnections` fallback (that group is curated by hand in `RbacSeedData.Permissions` instead of being swept into auto-discovery).
- **`DynamicSourceSystemPermissionAttribute(Type enumType, PermissionActionCode action, string? description)`** (`src/Api/FHIRBridge.Api/Rbac/DynamicSourceSystemPermissionAttribute.cs`) — metadata only, does **not** extend `AuthorizeAttribute` and enforces nothing by itself. It only tells `PermissionCatalog` which enum type + action to cross (via `AllGroupsFor`) so one permission per resolved group is auto-discovered at startup.
- **`ControllerAuthorizationExtensions.AuthorizePermissionAsync`** (`src/Api/FHIRBridge.Api/Security/ControllerAuthorizationExtensions.cs`) — the actual enforcement, called **imperatively inside the action body** (never automatic, since there's no attribute-based path for a runtime-resolved group):
  ```csharp
  var denied = await this.AuthorizePermissionAsync(_authorizationService, request.SourceSystemType, PermissionActionCode.Edit);
  if (denied is not null) return denied;
  ```
  This overload takes the enum value directly, resolves the group via `SourceSystemPermissionGroups.GroupFor` internally, then delegates to the `(PermissionGroupCode, PermissionActionCode)` overload, which builds the code and calls `IAuthorizationService.AuthorizeAsync` against the same `HasPermission:{code}` policy every other permission check uses — returning `controller.Forbid()` on failure, `null` on success. Because both the attribute (declared once, read at startup) and this call (evaluated per request) resolve the group the exact same way, they can never disagree about which permissions exist versus which get checked.

**Proven end to end this session:** adding `PermissionGroupCode.Allscripts` (matching the pre-existing `SourceSystemType.Allscripts`, which had no dedicated permission group before) produced a working `allscripts.edit` permission with **zero other code changes** — no attribute edit, no seed-data edit, no controller change. See [06 — Adding Permissions](06-adding-permissions-howto.md) for the full walkthrough.

## Request-time authorization flow (a real request, end to end)

Everything above is about how permissions get *into* the database. This is how a live HTTP request is actually checked against them.

**1. Login issues the token.**

```
LocalAuthService.LoginAsync (src/FHIRBridge.Application/Services/LocalAuthService.cs)
  │
  ├─ GetPermissionCodesAsync(userId, roleIds)
  │     roleCodes      = union of every role's granted Permission.Name
  │     userAllocations = this user's direct PermissionAllocation rows
  │     result = (roleCodes − any code the user has an allocation for) ∪ every allocation the user has IsEnabled = true
  │     → a direct user-level override always wins over the role default, whether granting or denying
  │
  └─ JwtAccessTokenIssuer.Issue(user, roleNames, permissionCodes)
        one "permissions" claim per code, e.g. Claim("permissions", "epic.edit")
        → JWT returned to the client
```

The merge happens once, at login — a permission override made after that only takes effect on the user's *next* login (the current access token is unaffected; see `PermissionEnforcementTests.Granting_a_direct_override__adds_the_permission_on_next_login_only`).

**2. Every subsequent request re-checks the token's claims — nothing is trusted from before.**

```
Incoming request
  │
  ├─ app.UseAuthentication()                     validates the JWT, populates HttpContext.User with its claims
  │
  ├─ password-change-required gate (Program.cs)  403s any /api/v1/* call (except change-password/me) if the
  │                                               "pwd_change_required" claim is "true"
  │
  ├─ app.UseAuthorization()                       evaluates [Authorize(Policy = "HasPermission:{code}")]
  │     │
  │     └─ PermissionAuthorizationHandler.HandleRequirementAsync (src/Api/FHIRBridge.Api/Security/)
  │           reads _currentUserService.CurrentUser.Permissions
  │             → HttpContextCurrentUserService filters HttpContext.User.Claims down to type "permissions"
  │           context.Succeed(requirement) if the required code is in that set (case-insensitive), else the
  │           request is rejected with 403 before the controller action ever runs
  │
  └─ Controller action runs
        for a [StandardPermission]-gated action: nothing further to do, already authorized above
        for a [DynamicSourceSystemPermission]-gated action (e.g. AddSourceConnection): the action body calls
        this.AuthorizePermissionAsync(...) itself — same policy machinery, just invoked with a group resolved
        from the request instead of one fixed at startup (see "The dynamic / resource-based mechanism" above)
```

**3. The Angular portal's role in this.** The portal never calls a "check my permission" endpoint at request time — it decodes the `permissions` claim array out of the JWT once (on login and on app boot) into its own `AuthStore`, and every `AuthService.hasPermission('epic.edit')` call afterward is a local string comparison against that decoded array, not a network call. This means the frontend's view of "what am I allowed to do" is a snapshot from login time too, same as the backend's token — consistent with point 1 above, just on the other side of the wire.

## What happens when the same `[StandardPermission]` is declared on multiple methods

This is fully supported, by design — it's normal for the same permission to gate more than one endpoint.

1. **Same Group+Action, same description text on every usage** — `Instances` accumulates every declaring method; `Description` stays that one text (duplicates are deduplicated before joining).
2. **Same Group+Action, different description text across usages** — `Description` becomes all of the distinct texts joined with `" | "`. Nothing is silently dropped — an earlier version of `PermissionCatalog` used `.DistinctBy(p => p.Code)`, which kept only the *first* discovered attribute's description and silently threw away every other one. That bug is what this combining behavior replaced.
3. **On a database row already synced from a previous boot** — if a *new* usage adds description text not already present, `RbacBootstrapper`/`SyncDiscoveredPermissionsAsync` append it (`"{existing} | {new}"`), guarded so re-appending the same text on the next boot is a no-op.
4. **Removing all but one usage** — `Instances` drops the removed location on the next boot; the permission itself stays active as long as at least one usage still references it.
5. **Removing every usage** — the permission is deactivated (see "Deactivate orphans" above), not deleted.

## Duplicate-definition validation

File: `src/FHIRBridge.Application/Rbac/RbacDefinitionValidator.cs`

The entire scheme above is a *pure function* of the taxonomy enums — which means a mistake **inside** that taxonomy (not in how it's used) can silently corrupt every Id/Code derived from it. `RbacDefinitionValidator.Validate()` runs at the very top of `RbacBootstrapper.EnsureAsync`, before any database write, and checks:

1. **Duplicate underlying enum value** — C# allows two members of the same enum to share an int (`Epic = 10, Foo = 10`); when that happens, `.ToString()`/`(int)` conversions can no longer tell them apart, so `PermissionTaxonomy`'s Id/Code derivation would collapse two different concepts into one row. Checked for `PermissionCategoryCode`, `PermissionGroupCode`, `PermissionActionCode` — via reflection over the enum's declared fields directly (`Enum.GetValues` alone can't recover the two colliding member *names* once they share a value, since it renders both as the same first-declared name).
2. **Duplicate declared Id** — a copy-pasted `[PermissionCategory("guid", ...)]`/`[PermissionGroup("guid", ...)]` where the Guid literal wasn't changed for the new member. Checked for `PermissionCategoryCode` and `PermissionGroupCode` (the two enums that carry an explicit Id).
3. **Duplicate display name** (case-insensitive) — not a data-corruption risk like the two above, but almost always a copy-paste mistake that leaves two different permissions indistinguishable in the permission-management UI. Checked for all three enums.
4. **Duplicate `(Group, Action)` pair in `RbacSeedData.Permissions`** — since a `PermissionSeed`'s Id/Name/Code are all derived from that pair (never hand-picked), two entries declaring the same pair produce the exact same duplicate code.

If `Validate()` returns any errors, `RbacBootstrapper.EnsureAsync` logs each one with `ILogger.LogError` and returns immediately — **no** category, group, or permission row is written that boot. The database is left exactly as it was (existing permissions keep working) rather than risk seeding data derived from an ambiguous definition. Fix the duplicate and the next boot proceeds normally.

Unit-tested in `tests/FHIRBridge.UnitTests/Security/RbacDefinitionValidatorTests.cs` against small, private, deliberately-broken test enums (never the real production enums) for each of the four cases, plus one live regression test (`Validate_reports_no_errors_for_the_real_taxonomy`) asserting today's real definitions are already clean.

## Files created or changed, and why

### Taxonomy definition (Application layer)

| File | What it is | Why |
|---|---|---|
| `src/FHIRBridge.Application/Rbac/PermissionCategoryCode.cs` | Enum of valid categories; each member carries `[PermissionCategory(id, displayName)]` | Only source of valid category values; the attribute makes a category's entire identity (Id + display text) a one-line, one-file addition |
| `src/FHIRBridge.Application/Rbac/PermissionGroupCode.cs` | Enum of valid groups; each member carries `[PermissionGroup(id, category, displayName)]` | Same idea, one tier down — also declares which category owns the group. A vendor group's member name (Epic, Cerner, Athenahealth, ...) must match its `SourceSystemType` counterpart exactly — see the dynamic mechanism above |
| `src/FHIRBridge.Application/Rbac/PermissionActionCode.cs` | Enum of valid actions; each member carries `[PermissionDisplayName(displayName)]` | Actions don't need their own Id (never a row by themselves — see `PermissionTaxonomy.BuildPermissionId`), just a display label |
| `src/FHIRBridge.Application/Rbac/PermissionCategoryAttribute.cs` | Attribute type | Carries a category's stable Guid + display name |
| `src/FHIRBridge.Application/Rbac/PermissionGroupAttribute.cs` | Attribute type | Carries a group's stable Guid + owning category + display name |
| `src/FHIRBridge.Application/Rbac/PermissionDisplayNameAttribute.cs` | Attribute type (action-only) | Carries an action's display text |
| `src/FHIRBridge.Application/Rbac/PermissionTaxonomy.cs` | The generator: `BuildPermissionId`/`BuildPermissionCode`/`BuildPermissionDisplayName`, plus `GetId`/`GetDisplayName`/`GetCategory` reflection helpers | Single source of truth for turning a `(Group, Action)` pair into everything a `Permission` row needs |
| `src/FHIRBridge.Application/Rbac/RbacDefinitionValidator.cs` | Duplicate-value/Id/display-name/seed-pair checker | Runs before every write; see "Duplicate-definition validation" above |
| `src/FHIRBridge.Application/Rbac/RbacSeedData.cs` | Assembles `Categories`/`Groups`/`Permissions`/`Roles`/`RolePermissions` from the enums + taxonomy | The canonical list `RbacBootstrapper` reads on every boot |

### The dynamic / resource-based mechanism (Application + Api layers)

| File | What it is | Why |
|---|---|---|
| `src/FHIRBridge.Application/Rbac/SourceSystemPermissionGroups.cs` | `GroupFor(Enum)` / `AllGroupsFor(Type)` — name-based enum-to-`PermissionGroupCode` resolution | Lets any enum's values (today: `SourceSystemType`) drive auto-discovered permissions with no hand-maintained mapping table |
| `src/Api/FHIRBridge.Api/Rbac/DynamicSourceSystemPermissionAttribute.cs` | Metadata-only attribute declaring an enum type + action for `PermissionCatalog` to cross | Tells the catalog which permissions to auto-discover; enforces nothing itself |
| `src/Api/FHIRBridge.Api/Security/ControllerAuthorizationExtensions.cs` | `AuthorizePermissionAsync` — imperative check, one overload for `(PermissionGroupCode, action)`, one for `(Enum value, action)` | The actual enforcement for a resource-based endpoint, called from inside the action body |

### Boot-time execution and static discovery (Api + Infrastructure layers)

| File | What it is | Why |
|---|---|---|
| `src/FHIRBridge.Infrastructure/Rbac/RbacBootstrapper.cs` | Runtime executor for `RbacSeedData` (see boot-time flow above) | Self-provisions/self-heals the built-in permission set on every boot, gated by `RbacDefinitionValidator` |
| `src/Api/FHIRBridge.Api/Rbac/PermissionCatalog.cs` | Reflects over `[StandardPermission]` and `[DynamicSourceSystemPermission]` usages, combines duplicates, distinguishes static vs. dynamic origin | Feeds `SyncDiscoveredPermissionsAsync`, the startup policy registration, and the typo-detection check — all in `Program.cs` |
| `src/Api/FHIRBridge.Api/Rbac/StandardPermissionAttribute.cs` | The attribute for a fixed, compile-time-known `(Group, Action)` | What `PermissionCatalog`'s static pass discovers |
| `src/Api/FHIRBridge.Api/Program.cs` | `SyncDiscoveredPermissionsAsync` + policy registration loop + `FindUndeclaredCodes` typo warning | Runtime executor for discovered permissions (static and dynamic) |
| `src/FHIRBridge.Domain/Entities/Permission.cs` | `IsActive`, `Instances`, `UpdateName`/`UpdateDescription`/`UpdateInstances`/`Activate`/`Deactivate` | The entity fields/mutators the sync logic above needs |
| `src/FHIRBridge.Infrastructure/Persistence/Configurations/PermissionConfiguration.cs` | Mapped `IsActive`/`Instances`; removed the unique index on `Name` | `Id` (the primary key) already is the deterministic encoding of `Group`+`Action`, so Id uniqueness alone guarantees at most one row per pair — a separate Name-uniqueness constraint was redundant and blocked the self-heal |
| `src/FHIRBridge.Application/Abstractions/Persistence/IUserAccessRepository.cs` + `EfUserAccessRepository.cs` / `InMemoryUserAccessRepository.cs` | `UpdatePermissionAsync` | The sync logic needs to mutate already-persisted rows, not just insert new ones |
| Migrations: `AddPermissionIsActive`, `AddPermissionInstances`, `FilterPermissionsNameIndexByActive`, `DropPermissionsNameUniqueConstraint`, `AddPermissionCategoryGroupHierarchy`, `AddPermissionDisplayNames`, `RegeneratePermissionIds` | Schema changes for the above | Applied in that order as the design evolved |

### Request-time authorization (Application + Api layers)

| File | What it is | Why |
|---|---|---|
| `src/FHIRBridge.Application/Services/LocalAuthService.cs` (`GetPermissionCodesAsync`) | Merges role-granted codes with the user's direct `PermissionAllocation` overrides at login | Produces the exact set of codes that goes into the JWT — an override always beats the role default |
| `src/Api/FHIRBridge.Api/Security/JwtAccessTokenIssuer.cs` | Adds one `"permissions"` claim per code to the issued JWT | The wire format every downstream check (backend and frontend) reads |
| `src/FHIRBridge.Application/Security/AuthorizationPolicies.cs` | `HasPermission(code)` → `"HasPermission:{code}"` policy name | Single place the policy-naming convention is defined |
| `src/Api/FHIRBridge.Api/Security/PermissionRequirement.cs` | `IAuthorizationRequirement` carrying one permission code | The requirement object every `HasPermission:*` policy is built from |
| `src/Api/FHIRBridge.Api/Security/PermissionAuthorizationHandler.cs` | `AuthorizationHandler<PermissionRequirement>` — the actual per-request check | Succeeds if the code is present in `ICurrentUserService.CurrentUser.Permissions` |
| `src/Api/FHIRBridge.Api/Security/HttpContextCurrentUserService.cs` | `ICurrentUserService` implementation reading `HttpContext.User.Claims` | Filters down to `"permissions"`-typed claims — the bridge between the validated JWT and the handler above |
| `src/Api/FHIRBridge.Api/Program.cs` (password-change gate, `app.UseAuthentication()`/`app.UseAuthorization()` ordering) | Middleware pipeline | Establishes exactly when in the pipeline permissions become available/enforced (see "Request-time authorization flow" above) |

### Frontend consumption (Angular portal)

| File | What it is | Why |
|---|---|---|
| `portal/src/app/auth/services/token.service.ts` | Decodes the JWT (`atob`/`JSON.parse`, no library) | Turns the raw access token string into a plain claims object |
| `portal/src/app/auth/services/jwt-user.mapper.ts` | `buildUserFromJwt` — reads the `permissions` claim array into `Permission[]` (`id`/`name` both set to the same code string, since the JWT carries no separate Id) | The frontend's `User` model shape |
| `portal/src/app/auth/store/auth.store.ts` | Signal-based store; `permissions` computed signal; `hasPermission`/`hasAnyPermission`/`hasRole`/`isAdmin` | Where every frontend permission check ultimately reads from |
| `portal/src/app/auth/services/auth.service.ts` | Thin public wrapper over the store (`hasPermission`, `isAdmin`, ...) | What components/guards actually call |
| `portal/src/app/auth/guards/permission.guard.ts` | `CanActivateFn` reading a route's `data.permissions`/`data.requireAll` | Route-level gating, admin-short-circuited, using the same `hasPermission` |

## Why the Id looks the way it does

`PermissionTaxonomy.BuildPermissionId(group, action)`:

```csharp
Guid.Parse($"{(int)group:D8}-0000-0000-0000-{(int)action:D12}");
```

- Group occupies the first 8 digits, Action the last 12; the two middle segments are always zero.
- **Category is deliberately excluded.** A group's owning category is a mutable fact (`PermissionGroupAttribute`'s `category` can be re-pointed in code, and `RbacBootstrapper` re-syncs `CategoryId` on the next boot) — not part of a permission's identity. Baking it into the Id would make the Id stale the moment that mapping changes.
- **`"D"` (decimal), not `"X"` (hex).** Decimal digits (`0`-`9`) are always valid hex characters too, so the resulting string is still a well-formed Guid — but every digit reads back as the enum's literal int value. With hex formatting, any value ≥ 10 would render as a letter (`10` → `a`); with decimal formatting it stays `10`. Example: `Epic` (group = 10) + `Read` (action = 11) → `00000010-0000-0000-0000-000000000011`.
- The same `Action` reused under a different `Group` naturally produces a different Id (the group digits differ) — e.g. `View` under both `User` and `Role` never collides.
