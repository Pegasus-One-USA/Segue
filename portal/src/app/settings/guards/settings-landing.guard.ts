// settings/guards/settings-landing.guard.ts
import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';
import { map } from 'rxjs';
import { AuthStore } from '../../auth/store/auth.store';
import { FullAccessResolverService } from '../../auth/services/full-access-resolver.service';

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
    const store         = inject(AuthStore);
    const router        = inject(Router);
    const fullAccessSvc = inject(FullAccessResolverService);

    // Pure function of the one thing that can't be decided synchronously (does this caller have
    // elevated -- SuperAdmin OR Full System Access -- settings access); every other candidate still
    // resolves exactly as before this fix. No side effects, safe to call more than once.
    const resolveWith = (hasElevatedAccess: boolean): UrlTree => {
      for (const candidate of candidates) {
        const allowed = candidate.superAdminOnly
          ? hasElevatedAccess
          : !candidate.permissions?.length || store.isAdmin() || candidate.permissions.some(p => store.hasPermission(p));

        if (allowed) {
          return router.parseUrl(`${basePath}/${candidate.path}`);
        }
      }

      return router.parseUrl('/unauthorized');
    };

    // RBAC Fix 4: a candidate's `superAdminOnly` flag used to mean exactly "store.hasRole('SuperAdmin')".
    // SuperAdminOnlyAuthorizationHandler (backend) now ALSO accepts any role with IsFullAccess=true (see
    // super-admin.guard.ts's matching RBAC Fix 3), so a custom "Full System Access" role is a real,
    // backend-authorized end state for these same candidates -- this guard was the one piece of the
    // settings-landing default-tab logic never updated to match.
    //
    // Fast path 1: a literal SuperAdmin claim resolves everything synchronously, exactly as before this
    // fix -- zero extra API calls, unchanged for the most common admin case.
    if (store.hasRole('SuperAdmin')) {
      return resolveWith(true);
    }

    // Fast path 2: resolveWith only reads the elevated-access flag when it actually walks as far as a
    // superAdminOnly candidate without an earlier one already matching. Trying both possible answers and
    // comparing them tells us, with no guesswork, whether this specific caller's real Full-Access status
    // could ever change the outcome. In every real candidate list in this app, superAdminOnly entries are
    // listed last (see settings.routes.ts) -- so the overwhelming majority of callers match an earlier
    // permission-gated candidate here and never trigger the API call below at all, exactly as cheap as
    // this guard always was.
    const ifNotElevated = resolveWith(false);
    if (router.serializeUrl(ifNotElevated) === router.serializeUrl(resolveWith(true))) {
      return ifNotElevated;
    }

    // Only reached when the outcome genuinely depends on this caller's real Full Access status.
    // Resolved via the shared FullAccessResolverService (see that file for why it matches by role
    // NAME, never displayName or id), cross-referenced against the roles this session's own claims
    // say it holds. Fails closed (treated as not elevated, same as today's plain "not SuperAdmin"
    // outcome) on any lookup error.
    const heldRoleNames = new Set(store.roles().map(r => r.name));

    return fullAccessSvc.resolve(heldRoleNames).pipe(
      map(hasFullAccess => resolveWith(hasFullAccess)),
    );
  };
}
