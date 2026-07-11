/** Both permission directives accept either a single code or a list — this is the one place
 *  that normalizes the raw template input before it reaches PermissionService. */
export function normalizePermissionInput(value: string | readonly string[] | null | undefined): string[] {
  if (!value) return [];
  return Array.isArray(value) ? [...value] : [value as string];
}
