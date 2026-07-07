# 05 — Permission Auto-Generation

> **Audience:** developers touching RBAC/permission code.
> **Scope:** how a permission's identity (Id, Name, DisplayName) is generated, how it gets into the database, and every file involved. See [06 — Adding Permissions](06-adding-permissions-howto.md) for the practical how-to.

## The problem this solves

Before this system, every permission needed a hand-picked GUID, a hand-typed wire-format code, and a hand-typed display name — three places to keep in sync, and nothing stopped a typo (`[StandardPermission("usr.viw")]`) from silently registering a policy nobody could ever satisfy.

Now: a permission's entire identity is a **pure function of two enum values** (`PermissionGroupCode`, `PermissionActionCode`). Given the same pair, you always get the same Id, the same wire-format code, and the same display name — in every environment, on every boot, with nobody hand-picking anything.

## The two pipelines

There are **two independent ways a permission comes to exist**, and they never touch the same row:

| | Built-in permissions | Discovered permissions |
|---|---|---|
| Declared in | `RbacSeedData.Permissions` (a C# list) | `[StandardPermission]` attributes on controllers |
| `IsSystem` | `true` | `false` |
| Owned/synced by | `RbacBootstrapper` | `Program.cs` → `SyncDiscoveredPermissionsAsync` |
| Runs when | Every boot, before discovery sync | Every boot, after `RbacBootstrapper` |
| Typical use | Permissions you've deliberately promoted into the canonical seed list (currently 20) | Any `[StandardPermission]` usage that *isn't* in that list yet |

A permission can start as **discovered-only**, and later be promoted into `RbacSeedData.Permissions` — at that point `SyncDiscoveredPermissionsAsync` stops touching it entirely (see "Ownership handoff" below) and `RbacBootstrapper` takes over for good.

## Boot-time flow

```
Program.cs (top level)
  builder.Build() → app
  │
  ├─ BootstrapDatabase(app)
  │    ├─ dbContext.Database.Migrate()                  (apply pending EF migrations)
  │    └─ IRbacBootstrapper.EnsureAsync()                → RbacBootstrapper.EnsureAsync (see below)
  │
  └─ SyncDiscoveredPermissions(app)
       └─ SyncDiscoveredPermissionsAsync(repository, logger)   (see below)
```

Both run synchronously (`.GetAwaiter().GetResult()`) during startup, before `app.Run()` — a request is never served against a partially-synced permission table.

### `RbacBootstrapper.EnsureAsync` (built-in permissions)

File: `src/FHIRBridge.Infrastructure/Rbac/RbacBootstrapper.cs`

1. **Categories** — for each `RbacSeedData.Categories` entry, find the existing row by Id; if found, re-sync `DisplayName`; if not found, insert. Save.
2. **Groups** — same pattern, plus re-syncs `CategoryId` if a group's owning category changed in code (`PermissionGroupAttribute`'s `category` argument). Save.
3. **Permissions** — for each `RbacSeedData.Permissions` entry:
   - Look up an existing row **by Id first, then by Name** (`existingPermissionsByName`, built by indexer assignment so a Name shared by more than one row — active or not — never throws). The Name fallback is what makes this self-healing: if the Id-derivation formula ever changes (it has, more than once, this project's history), an already-seeded permission is still recognized as "the same permission" and never duplicated.
   - If found: re-sync `DisplayName`/`GroupId`; **merge** (never overwrite) `Description` if the seed's text isn't already a substring of what's stored; **reactivate** it if it was previously deactivated.
   - If not found: insert a new row with `IsSystem = true`.
   - Either way, record `seed.Id → actual row Id` in `actualPermissionIdBySeedId` — needed because the *seed's* theoretical Id and the *row's* real Id can differ after a Name-fallback match.
4. **Deactivate orphans** — any `IsSystem = true`, currently-active row whose Id wasn't matched by any `RbacSeedData.Permissions` entry this boot gets `Deactivate()`'d (never deleted — `PermissionAllocations` referencing it, i.e. role grants and audit history, stay intact).
5. **Roles** — insert any of the 4 built-in roles (`RbacSeedData.Roles`) missing by Id.
6. **Role→Permission grants** — for each `RbacSeedData.RolePermissions` entry, translate the seed's permission Id through `actualPermissionIdBySeedId` (step 3) before checking/inserting the grant — this is what keeps a role's grants pointed at the right row even when a permission's Id drifted.

### `SyncDiscoveredPermissionsAsync` (discovered-only permissions)

File: `src/Api/FHIRBridge.Api/Program.cs`

1. Call `PermissionCatalog.DiscoveredPermissions(assembly)` — reflects over every controller class/method for `[StandardPermission]` attributes (see "What discovery actually does" below for the multi-declaration behavior).
2. Build `seedDeclaredPermissionIds` — every Id already in `RbacSeedData.Permissions`. **Anything in this set is skipped entirely** by this method, both in the deactivation pass and the create/update pass — it's `RbacBootstrapper`'s territory, not this method's. This is the fix for a real bug: before this guard existed, this method would try to "self-heal" a built-in permission the same way it heals a discovered one, which meant deactivating the real, active, `IsSystem = true` row and inserting a *second*, `IsSystem = false` row under the same Name — corrupting the row's system-ness and causing a dictionary-duplicate-key crash the next time `RbacBootstrapper` ran.
3. **Deactivate orphans** — a currently-active, non-seed-declared permission not present in this boot's discovered set gets deactivated. (Re-declaring it later reactivates the same row — see step 4.)
4. **Update or create** — for each discovered permission (already deduplicated/combined by `PermissionCatalog`, see below):
   - Found by Id → re-sync `Name`/`DisplayName`/`Description`/`Instances`; reactivate if it was inactive.
   - Not found → insert a new `IsSystem = false` row, and grant it to the SuperAdmin role automatically. If it has no description (i.e. every `[StandardPermission]` declaring it omitted one), log a warning — this is meant to be caught in review, not silently accepted.

### What `PermissionCatalog.DiscoveredPermissions` actually does

File: `src/Api/FHIRBridge.Api/Rbac/PermissionCatalog.cs`

This method does the **combining**, not `Program.cs` — by the time `SyncDiscoveredPermissionsAsync` sees a `DiscoveredPermission`, duplicates are already merged:

1. Reflect over every `ControllerBase`-derived type in the assembly; collect every `[StandardPermission]` on the class itself and on each public instance method, each tagged with its declaring location (`"UsersController"` for a class-level attribute, `"UsersController.InviteUser"` for a method-level one).
2. Group all of these occurrences by their wire-format `PermissionCode` (case-insensitive) — i.e. by the same `(Group, Action)` pair.
3. For each group, produce **one** `DiscoveredPermission`:
   - `Description` = every attribute's non-empty description in that group, deduplicated and joined with `" | "` (or `null` if none supplied one).
   - `Instances` = every declaring location, deduplicated and joined with `", "`.
   - `Group`/`Action`/`Code` come from the first occurrence (they're identical across the group by construction — same `PermissionCode` implies same `Group`+`Action`).

## What happens when the same `[StandardPermission]` is declared on multiple methods

This is fully supported, by design — it's normal for the same permission to gate more than one endpoint.

1. **Same Group+Action, same description text on every usage** — `Instances` accumulates every declaring method; `Description` stays that one text (duplicates are deduplicated before joining).
2. **Same Group+Action, different description text across usages** — `Description` becomes all of the distinct texts joined with `" | "` (e.g. `"Update a user's profile information. | Update a user's profile information 1."`). Nothing is silently dropped — see the history of this: an earlier version of `PermissionCatalog` used `.DistinctBy(p => p.Code)`, which kept only the *first* discovered attribute's description and silently threw away every other one. That bug is what this combining behavior replaced.
3. **On a database row already synced from a previous boot** — if a *new* usage adds description text not already present, `RbacBootstrapper`/`SyncDiscoveredPermissionsAsync` append it (`"{existing} | {new}"`), guarded so re-appending the same text on the next boot is a no-op — the stored `Description` only grows when genuinely new text appears.
4. **Removing all but one usage** — `Instances` drops the removed location on the next boot; the permission itself stays active as long as at least one `[StandardPermission]` still references it.
5. **Removing every usage** — the permission is deactivated (see "Deactivate orphans" above), not deleted.

## Files created or changed, and why

| File | What changed | Why |
|---|---|---|
| `src/FHIRBridge.Application/Rbac/PermissionCategoryCode.cs` | Enum of valid categories; each member carries `[PermissionCategory(id, displayName)]` | Only source of valid category values; the attribute makes a category's entire identity (Id + display text) a one-line, one-file addition |
| `src/FHIRBridge.Application/Rbac/PermissionGroupCode.cs` | Enum of valid groups; each member carries `[PermissionGroup(id, category, displayName)]` | Same idea, one tier down — also declares which category owns the group |
| `src/FHIRBridge.Application/Rbac/PermissionActionCode.cs` | Enum of valid actions; each member carries `[PermissionDisplayName(displayName)]` | Actions don't need their own Id (they're never a row by themselves — see `PermissionTaxonomy.BuildPermissionId`), just a display label |
| `src/FHIRBridge.Application/Rbac/PermissionCategoryAttribute.cs` | New attribute type | Carries a category's stable Guid + display name |
| `src/FHIRBridge.Application/Rbac/PermissionGroupAttribute.cs` | New attribute type | Carries a group's stable Guid + owning category + display name |
| `src/FHIRBridge.Application/Rbac/PermissionDisplayNameAttribute.cs` | Attribute type (now action-only) | Carries an action's display text |
| `src/FHIRBridge.Application/Rbac/PermissionTaxonomy.cs` | The actual generator: `BuildPermissionId`/`BuildPermissionCode`/`BuildPermissionDisplayName`, plus `GetId`/`GetDisplayName`/`GetCategory` reflection helpers that read the attributes above | Single source of truth for turning a `(Group, Action)` pair into everything a `Permission` row needs |
| `src/FHIRBridge.Application/Rbac/RbacSeedData.cs` | Assembles `Categories`/`Groups`/`Permissions`/`Roles`/`RolePermissions` from the enums + taxonomy | The canonical list `RbacBootstrapper` reads on every boot |
| `src/FHIRBridge.Infrastructure/Rbac/RbacBootstrapper.cs` | Runtime executor for `RbacSeedData` (see flow above) | Self-provisions/self-heals the built-in permission set on every boot |
| `src/Api/FHIRBridge.Api/Rbac/PermissionCatalog.cs` | Reflects over `[StandardPermission]` usages, combines duplicates | Feeds `SyncDiscoveredPermissionsAsync` and the startup policy registration (`Program.cs`) |
| `src/Api/FHIRBridge.Api/Rbac/StandardPermissionAttribute.cs` | The attribute controllers apply | What `PermissionCatalog` discovers |
| `src/Api/FHIRBridge.Api/Program.cs` | `SyncDiscoveredPermissionsAsync` (+ policy registration loop) | Runtime executor for discovered-only permissions (see flow above) |
| `src/FHIRBridge.Domain/Entities/Permission.cs` | Added `IsActive`, `Instances`, `UpdateName`/`UpdateDescription`/`UpdateInstances`/`Activate`/`Deactivate` | The entity fields/mutators the sync logic above needs |
| `src/FHIRBridge.Infrastructure/Persistence/Configurations/PermissionConfiguration.cs` | Mapped `IsActive`/`Instances`; **removed** the unique index on `Name` entirely | `Id` (the primary key) already is the deterministic encoding of `Group`+`Action`, so Id uniqueness alone already guarantees at most one row per pair — a separate Name-uniqueness constraint was redundant, and blocked the self-heal (a deactivated row and a fresh active row need to be able to share a Name) |
| `src/FHIRBridge.Application/Abstractions/Persistence/IUserAccessRepository.cs` + `EfUserAccessRepository.cs` / `InMemoryUserAccessRepository.cs` | Added `UpdatePermissionAsync` | The sync logic above needs to mutate already-persisted rows (`Activate`/`Deactivate`/`UpdateInstances`/…), not just insert new ones |
| Migrations: `AddPermissionIsActive`, `AddPermissionInstances`, `FilterPermissionsNameIndexByActive`, `DropPermissionsNameUniqueConstraint` | Schema changes for the above | Applied in that order as the design evolved — the third (a filtered "unique among active rows" index) was superseded by the fourth once it became clear a permission that's *both* seed-declared and actively referenced has no inactive row to make room for, so even a filtered unique index wasn't enough |

## Why the Id looks the way it does

`PermissionTaxonomy.BuildPermissionId(group, action)`:

```csharp
Guid.Parse($"{(int)group:D8}-0000-0000-0000-{(int)action:D12}");
```

- Group occupies the first 8 digits, Action the last 12; the two middle segments are always zero.
- **Category is deliberately excluded.** A group's owning category is a mutable fact (`PermissionGroupAttribute`'s `category` can be re-pointed in code, and `RbacBootstrapper` re-syncs `CategoryId` on the next boot) — not part of a permission's identity. Baking it into the Id would make the Id stale the moment that mapping changes.
- **`"D"` (decimal), not `"X"` (hex).** Decimal digits (`0`-`9`) are always valid hex characters too, so the resulting string is still a well-formed Guid — but every digit reads back as the enum's literal int value. With hex formatting, any value ≥ 10 would render as a letter (`10` → `a`); with decimal formatting it stays `10`. Example: `Epic` (group = 10) + `Read` (action = 11) → `00000010-0000-0000-0000-000000000011`.
- The same `Action` reused under a different `Group` naturally produces a different Id (the group digits differ) — e.g. `View` under both `User` and `Role` never collides.
