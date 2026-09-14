import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of } from 'rxjs';
import { IRoleService } from '../../user-management/services/i-role.service';

/**
 * RBAC review fix: resolves whether a set of currently-held role NAMES includes any role with Full
 * System Access — the one piece of logic that was previously duplicated, near-identically, across
 * super-admin.guard.ts, settings-landing.guard.ts, settings-shell.component.ts,
 * system-settings-shell.component.ts, user-detail.component.ts, and role-dialog.component.ts.
 * Extracted here so the matching rule only has to be correct — and only ever has to be fixed — once.
 *
 * Matches by `name`, never `displayName` and never `id`:
 *   - `name` is the one field consistently populated the same way on both sides of this comparison
 *     from the real backend `Role.Name`: api-user.service.ts's RoleDto mapping sets `name: dto.name`
 *     for the live catalog (IRoleService.getRoles()), and jwt-user.mapper.ts's JWT-claim mapping sets
 *     `name: toUserRole(rn)` — a case-normalizing pass-through of the same `roles` JWT claim the
 *     backend issued from that identical Role.Name.
 *   - `displayName` happens to equal `name` for every role sourced from either of those two paths
 *     today (RoleDto carries no separate DisplayName field at all), but relying on that coincidence
 *     is fragile — nothing stops a future change from giving Role a real, independent display label,
 *     and mock data already models exactly that divergence.
 *   - `id` must NEVER be used here: the JWT only ever carries role NAMES (see jwt-user.mapper.ts), so
 *     the logged-in user's own Role stand-ins set `id` to that name string as a placeholder — never a
 *     real database identifier — while IRoleService.getRoles() returns the actual database GUID as
 *     `id` for the very same role. The two id spaces never intersect, so comparing by `id` would
 *     always evaluate to false and silently deny Full Access to every caller, including a genuine
 *     Full-Access one.
 *
 * Fails closed (resolves `false`, never errors) on any getRoles() failure — network failure, or a
 * caller who lacks role.view so the endpoint itself 403s — exactly like a caller who simply isn't
 * Full Access.
 */
@Injectable({ providedIn: 'root' })
export class FullAccessResolverService {
  private readonly roleSvc = inject(IRoleService);

  /** @param heldRoleNames The current session's own role NAMES (never displayName or id) — e.g.
   *  `new Set(store.roles().map(r => r.name))`. */
  resolve(heldRoleNames: ReadonlySet<string>): Observable<boolean> {
    return this.roleSvc.getRoles().pipe(
      map(allRoles => allRoles.some(r => heldRoleNames.has(r.name) && r.isFullAccess)),
      catchError(() => of(false)),
    );
  }
}
