// data/terminology-feature.config.ts
//
// Single on/off switch for the entire Terminology Codes feature — System Settings' "Terminology
// Codes" tab and its LOINC/SNOMED CT/RxNorm/ICD-10-CM/ICD-10-PCS/HCPCS/NDC/CVX/UCUM/CPT children,
// plus the loinc.*/snomedct.*/rxnorm.*/icd10.* permissions those four RBAC-covered systems expose
// in every Role & Permissions view. Temporarily disabled app-wide — nothing behind it (routes,
// components, services, API endpoints, backend controllers, or permission definitions/grants) is
// removed; every place that shows or gates this feature reads this one constant, so flipping it
// back to `true` makes all of it visible/reachable again with no other code change:
//   - settings.routes.ts               (route guard + the system-settings shell's own OR-gate)
//   - settings-shell.component.ts       (top-level "System Settings" tab)
//   - system-settings-shell.component.ts (the "Terminology Codes" nav tab)
//   - sidebar.component.ts              (the sidebar's "Settings" entry)
//   - role-permissions.component.ts     (Role Permissions grid)
//   - user-detail.component.ts          (Effective Permissions)
//   - user-permission-overrides.component.ts (Direct Permission Overrides)
//   - assign-roles-dialog.component.ts  (Role Allocations preview)
export const TERMINOLOGY_FEATURE_ENABLED = false;

/** Wire-format permission codes for the four terminology systems with real RBAC coverage today —
 *  was duplicated across settings.routes.ts/settings-shell.component.ts/sidebar.component.ts;
 *  consolidated here so there's exactly one list to keep in sync with the backend's
 *  PermissionGroupCode.Loinc/SnomedCt/RxNorm/Icd10 members. */
export const TERMINOLOGY_PERMISSION_CODES: string[] = [
  'loinc.view', 'loinc.write',
  'snomedct.view', 'snomedct.write',
  'rxnorm.view', 'rxnorm.write',
  'icd10.view', 'icd10.write',
];

/** Permission-code "resource"/prefix segment for each terminology system — matches
 *  Permission.resource (see user.model.ts) and permission-matrix.config.ts's own
 *  terminologySystem() prefix argument (e.g. 'loinc', 'snomedct'). Used by every catalog-driven
 *  Role & Permissions view to filter these groups out the same way node-permission-visibility.util
 *  already filters phase-hidden connector groups. */
export const TERMINOLOGY_PERMISSION_PREFIXES: ReadonlySet<string> = new Set(['loinc', 'snomedct', 'rxnorm', 'icd10']);
