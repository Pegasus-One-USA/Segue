import { Injectable, inject } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { PermissionService } from './permission.service';
import { PermissionMode } from '../models/permission-check.model';
import { normalizePermissionInput } from '../directives/permission-input.util';

/**
 * Defense-in-depth check for the START of an action handler (click handler, submit
 * handler, etc.) — NOT a substitute for hiding (*appHasPermission) or disabling
 * ([appDisableWithoutPermission]) a control, and NOT a substitute for backend
 * authorization, which remains the real source of truth.
 *
 * This exists for the gap between "the control was rendered as allowed" and "the user
 * actually activated it" — e.g. a permission was revoked in another tab a moment ago and
 * this tab hasn't re-rendered yet, or a disabled control was still reachable via devtools
 * or a race in some third-party menu component. The backend will reject the request
 * regardless; this just turns that into an immediate, friendly message instead of a
 * silent no-op or a raw 403 surfacing from an HTTP error handler.
 */
@Injectable({ providedIn: 'root' })
export class PermissionActionGuard {
  private readonly permissionSvc = inject(PermissionService);
  private readonly snackBar = inject(MatSnackBar);

  /** Returns true if the action may proceed. If false, a toast explaining why has
   *  already been shown — the caller should just return, not show its own message. */
  ensure(codes: string | readonly string[], reason: string, mode: PermissionMode = 'any'): boolean {
    const allowed = this.permissionSvc.matches(mode, normalizePermissionInput(codes));
    if (!allowed) {
      this.snackBar.open(reason, 'Dismiss', { duration: 4000 });
    }
    return allowed;
  }
}
