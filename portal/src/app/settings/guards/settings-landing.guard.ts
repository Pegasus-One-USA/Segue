// settings/guards/settings-landing.guard.ts
import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthStore } from '../../auth/store/auth.store';

export interface SettingsLandingCandidate {
  /** Path segment, absolute-relative to `basePath` (no leading slash), to land on if reachable. */
  path: string;
  /** OR-list of permission codes that unlock this candidate. Omit for "every authenticated user". */
  permissions?: string[];
  /** True to require the SuperAdmin role specifically instead of a permission check — same
   *  semantics as sidebar.component.ts/settings-shell.component.ts's own `superAdminOnly`. */
  superAdminOnly?: boolean;
}

/**
 * Replaces a static `redirectTo: '<first-tab>'` default child route with one that actually
 * reflects the viewer's permissions. A static redirect always lands every viewer on the SAME
 * tab regardless of whether they can reach it — which then immediately bounces them to
 * /unauthorized if they can't. That hit every Settings/System Settings/Terminology Codes
 * sub-permission holder who lacked the specific default tab's own permission (not just the
 * newly split per-terminology-system permissions this was written for).
 *
 * Checked in the given order — put the most commonly-granted tab first. Falls through to
 * /unauthorized only if none of the candidates are reachable (shouldn't happen in practice: the
 * parent route the caller attaches this to is itself gated on the union of every candidate's
 * permissions, so reaching this guard at all already implies at least one candidate passes).
 */
export function settingsLandingGuard(basePath: string, candidates: SettingsLandingCandidate[]): CanActivateFn {
  return () => {
    const store  = inject(AuthStore);
    const router = inject(Router);

    for (const candidate of candidates) {
      const allowed = candidate.superAdminOnly
        ? store.hasRole('SuperAdmin')
        : !candidate.permissions?.length || store.isAdmin() || candidate.permissions.some(p => store.hasPermission(p));

      if (allowed) {
        return router.parseUrl(`${basePath}/${candidate.path}`);
      }
    }

    return router.parseUrl('/unauthorized');
  };
}
