// AUTO-GENERATED — do not hand-edit.
//
// Source of truth: src/FHIRBridge.Application/Rbac/PermissionGroupCode.cs and
// PermissionActionCode.cs on the backend (PermissionTaxonomy.BuildPermissionCode lowercases
// these same member names to build the wire-format code, e.g. "user.edit"). Regenerate after
// any group/action rename or addition there:
//   node scripts/generate-permissions.mjs
//
// A stale copy is caught in CI, not silently shipped — portal-ci.yml runs this same script
// with --check and fails the build if the committed file no longer matches the backend.

export enum PermissionGroup {
  User = 'user',
  Role = 'role',
  Workflow = 'workflow',
  Configuration = 'configuration',
  Pipeline = 'pipeline',
  AuditLogs = 'auditlogs',
  SourceConnections = 'sourceconnections',
  Report = 'report',
  Payload = 'payload',
  Epic = 'epic',
  Athenahealth = 'athenahealth',
  Cerner = 'cerner',
  Allscripts = 'allscripts',
  NewEHR = 'newehr',
}

export enum PermissionAction {
  View = 'view',
  Create = 'create',
  Edit = 'edit',
  Delete = 'delete',
  Invite = 'invite',
  Deactivate = 'deactivate',
  Assign = 'assign',
  Run = 'run',
  Execute = 'execute',
  Write = 'write',
  Read = 'read',
  Test = 'test',
}

/** Joins a group and action into the backend's wire-format permission code, e.g.
 *  `permissionCode(PermissionGroup.User, PermissionAction.Edit)` -> `"user.edit"`. */
export function permissionCode(group: PermissionGroup, action: PermissionAction): string {
  return `${group}.${action}`;
}
