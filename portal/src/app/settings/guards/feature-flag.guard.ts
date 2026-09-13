// settings/guards/feature-flag.guard.ts
import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';

/**
 * Blocks a route entirely while a feature flag is off — generic, not tied to any one feature.
 * Normal navigation never offers a link into a disabled route (see the corresponding nav-list
 * filters, which must stay in sync), and this guard is what actually stops a direct or bookmarked
 * URL from reaching it: instead of rendering, the router is redirected to `redirectTo`. Put this
 * guard FIRST in a route's `canActivate` array (before any permission guard) so a disabled feature
 * is unreachable regardless of what the viewer is otherwise permitted to do.
 *
 * Flip the flag passed in at the call site back to `true` to restore the route — nothing else
 * about this guard needs to change.
 */
export function featureFlagGuard(enabled: boolean, redirectTo: string): CanActivateFn {
  return () => (enabled ? true : inject(Router).parseUrl(redirectTo));
}
