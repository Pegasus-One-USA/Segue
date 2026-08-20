import { Injectable, inject, computed, isDevMode } from '@angular/core';
import { AuthStore } from '../store/auth.store';
import { PermissionMode } from '../models/permission-check.model';
import { SOURCES } from '../../data/sources.data';
import { TRANSFORMS } from '../../data/transforms.data';

/**
 * Centralized, fast permission evaluation. This is the ONLY place in the app that
 * should ever ask "does the current user have permission X" — HideWithoutPermissionDirective,
 * DisableWithoutPermissionDirective, and permissionGuard all funnel through here, and
 * AuthStore.hasPermission() delegates here too so there is exactly one implementation.
 *
 * Deliberately does NOT know what permission codes exist. It only compares opaque
 * strings the backend issued in the JWT against opaque strings a template asks about —
 * see CLAUDE.md / the architecture writeup for why hardcoding vendor names here would
 * defeat the backend's dynamic permission discovery.
 */
@Injectable({ providedIn: 'root' })
export class PermissionService {
  private readonly authStore = inject(AuthStore);

  /** O(1)-lookup index, rebuilt only when the authenticated user identity actually changes
   *  (login / logout / token refresh) — not on every check, not on every change-detection tick. */
  private readonly permissionSet = computed<Set<string>>(() => {
    const perms = this.authStore.currentUser()?.permissions ?? [];
    return new Set(perms.map(p => this.normalize(p.name)));
  });

  /** Read-only view for debugging / an admin "what can I do" panel. Never used for lookups. */
  readonly permissions = computed<readonly string[]>(() => Array.from(this.permissionSet()));

  /** Every permission-code prefix that represents a workflow "node" (a source vendor or destination
   *  type usable inside a workflow) rather than the Workflow module itself — derived from the same
   *  `permissionPrefix` fields sources.data.ts/transforms.data.ts already declare for Node Library tile
   *  gating, not a second hand-maintained list (per the "don't duplicate the node-prefix list" rule this
   *  screen was built against). Deliberate, sole exception to this service's "doesn't know what
   *  permission codes exist" rule above — "is this code a workflow node" is a structural question about
   *  the app's own catalog, not a specific vendor identity baked into permission-evaluation logic. */
  private readonly workflowNodePermissionCodes: readonly string[] = (() => {
    const prefixes = [
      ...SOURCES.map(s => s.permissionPrefix),
      ...TRANSFORMS.map(t => t.permissionPrefix),
    ].filter((p): p is string => !!p);
    const actions = ['view', 'create', 'edit', 'delete', 'execute'];
    return prefixes.flatMap(prefix => actions.map(action => `${prefix}.${action}`));
  })();

  /**
   * True when the current user can reach the Workflow module at all — either they hold the literal
   * `workflow.view` permission, or they hold ANY permission on ANY workflow node (any action, e.g.
   * `epic.view`, `sqlserver.edit`). This is the single source of truth for "module access," used by the
   * sidebar, the /workflows and /workflow-builder route guards, and the Node Library's open-gate —
   * matching the same rule the backend's WorkflowModuleAccessAuthorizationHandler enforces
   * independently. Does NOT imply any individual node's own view/create/edit/delete/execute permission,
   * nor any workflow.create/edit/delete/run — those all stay fully independent.
   */
  hasWorkflowModuleAccess(): boolean {
    return this.hasPermission('workflow.view') || this.hasAny(this.workflowNodePermissionCodes);
  }

  /** Always true today: permissions are decoded synchronously from the JWT before the app
   *  bootstraps (see APP_INITIALIZER in app.config.ts), so there's no async gap to model.
   *  Exposed as a real signal — not a hardcoded `true` in the directives — so that if the
   *  login flow ever moves to a separate permissions API call, every consumer keeps working
   *  by changing this one signal's source, instead of auditing every template in the app. */
  readonly isReady = computed<boolean>(() => true);

  hasPermission(code: string): boolean {
    return this.evaluate(code);
  }

  /** OR — true if the user holds at least one of the listed permissions. */
  hasAny(codes: readonly string[]): boolean {
    this.warnIfEmpty(codes, 'hasAny');
    return codes.some(c => this.evaluate(c));
  }

  /** AND — true only if the user holds every listed permission. Vacuously true for []
   *  (matches Array.prototype.every semantics) — same reasoning as `*ngIf` on an empty list. */
  hasAll(codes: readonly string[]): boolean {
    this.warnIfEmpty(codes, 'hasAll');
    return codes.every(c => this.evaluate(c));
  }

  /** NOT — true only if the user holds none of the listed permissions.
   *
   *  Implemented as `!hasAny(codes)` rather than its own admin-bypass branch on purpose:
   *  an admin is modeled as "effectively holds every permission" (see `evaluate`), so an
   *  admin must NOT satisfy "holds none of X" — they hold all of X. Special-casing admins
   *  to always return true here would be inverted and wrong (it would hide a
   *  "you lack permission" banner from everyone EXCEPT the one role that should never see
   *  it in the first place). Composing through `hasAny` gets this right for free. */
  hasNone(codes: readonly string[]): boolean {
    return !this.hasAny(codes);
  }

  /** Resolves a normalized PermissionCheck (mode + codes) — what the directives actually call. */
  matches(mode: PermissionMode, codes: readonly string[]): boolean {
    switch (mode) {
      case 'all':  return this.hasAll(codes);
      case 'none': return this.hasNone(codes);
      case 'any':
      default:     return this.hasAny(codes);
    }
  }

  /** Single evaluation primitive every public method composes from. Admins are treated as
   *  holding every permission — this replaces the `isAdmin() || hasPermission(...)` check
   *  that was previously duplicated at every call site (sidebar nav filter, permissionGuard,
   *  user-list's canEdit/canDelete/etc.) with one centralized rule. */
  private evaluate(code: string): boolean {
    if (this.authStore.isAdmin()) return true;
    const normalized = this.normalize(code);
    this.warnIfMiscased(code, normalized);
    return this.permissionSet().has(normalized);
  }

  private normalize(code: string): string {
    return code.trim().toLowerCase();
  }

  private warnIfMiscased(original: string, normalized: string): void {
    if (isDevMode() && original !== normalized) {
      console.warn(
        `[PermissionService] "${original}" was normalized to "${normalized}". ` +
        `Permission codes are always lowercase dot-separated (e.g. "epic.edit") — ` +
        `write them that way to avoid confusion, even though the check still works.`
      );
    }
  }

  private warnIfEmpty(codes: readonly string[], method: string): void {
    if (isDevMode() && codes.length === 0) {
      console.warn(`[PermissionService] ${method}() was called with an empty array — this is almost always a template binding mistake.`);
    }
  }
}
