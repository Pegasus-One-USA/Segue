/**
 * How a multi-permission check combines its inputs.
 *  - 'any'  → OR  — true if the user holds at least one of the listed permissions.
 *  - 'all'  → AND — true only if the user holds every listed permission.
 *  - 'none' → NOT — true only if the user holds none of the listed permissions.
 */
export type PermissionMode = 'any' | 'all' | 'none';

/** Normalized shape the directives resolve their template input into before asking the service. */
export interface PermissionCheck {
  codes: readonly string[];
  mode: PermissionMode;
}
