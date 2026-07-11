import { Directive, TemplateRef, ViewContainerRef, inject, input, computed, effect } from '@angular/core';
import { PermissionService } from '../services/permission.service';
import { PermissionMode } from '../models/permission-check.model';
import { normalizePermissionInput } from './permission-input.util';

/**
 * Structural directive — controls whether the host template exists in the DOM at all.
 * This is a UX convenience only; the backend re-validates every request regardless of
 * what this directive decided to render (see the architecture writeup, Section 1).
 *
 * Permission codes should come from the generated `PermissionGroup`/`PermissionAction`
 * constants (scripts/generate-permissions.mjs) via `permissionCode(...)`, not hand-typed
 * 'group.action' string literals — see permission.constants.ts.
 *
 * Single permission:
 *   <button *appHideWithoutPermission="permissionCode(PermissionGroup.Epic, PermissionAction.Edit)">Edit</button>
 *
 * OR — default mode for a list ("has any of these"):
 *   <button *appHideWithoutPermission="[permissionCode(PermissionGroup.Epic, PermissionAction.Edit), permissionCode(PermissionGroup.Epic, PermissionAction.Test)]">Edit</button>
 *
 * AND — explicit:
 *   <div *appHideWithoutPermission="[...]; mode: 'all'">…</div>
 *
 * NONE — "show this only if the user holds none of these":
 *   <div *appHideWithoutPermission="[permissionCode(PermissionGroup.Epic, PermissionAction.Edit)]; mode: 'none'">Read-only mode.</div>
 *
 * Fallback content instead of nothing:
 *   <div *appHideWithoutPermission="permissionCode(PermissionGroup.Workflow, PermissionAction.Run); else noAccess">…</div>
 *   <ng-template #noAccess>You don't have permission to run pipelines.</ng-template>
 */
@Directive({
  selector: '[appHideWithoutPermission]',
  standalone: true,
})
export class HideWithoutPermissionDirective {
  private readonly templateRef   = inject(TemplateRef<unknown>);
  private readonly viewContainer = inject(ViewContainerRef);
  private readonly permissionSvc = inject(PermissionService);

  readonly appHideWithoutPermission     = input.required<string | readonly string[]>();
  readonly appHideWithoutPermissionMode = input<PermissionMode>('any');
  readonly appHideWithoutPermissionElse = input<TemplateRef<unknown> | null>(null);

  /** Fails closed while permissions aren't ready yet (see PermissionService.isReady) — render
   *  nothing rather than briefly flash content the user may not be entitled to. */
  private readonly satisfied = computed(() => {
    if (!this.permissionSvc.isReady()) return false;
    const codes = normalizePermissionInput(this.appHideWithoutPermission());
    return this.permissionSvc.matches(this.appHideWithoutPermissionMode(), codes);
  });

  constructor() {
    // The only side-effecting piece of this directive: turning a boolean into actual
    // view-container mutations. Everything feeding `satisfied` is a pure computed(), so
    // this effect re-runs exactly when the permission set, the inputs, or the else
    // template change — never on unrelated change-detection cycles. `clear()` fully
    // destroys whatever view is currently there, so there's nothing to hold a reference
    // to between runs.
    effect(() => {
      const show = this.satisfied();
      const elseTemplate = this.appHideWithoutPermissionElse();

      this.viewContainer.clear();

      if (show) {
        this.viewContainer.createEmbeddedView(this.templateRef);
      } else if (elseTemplate) {
        this.viewContainer.createEmbeddedView(elseTemplate);
      }
    });
  }
}
