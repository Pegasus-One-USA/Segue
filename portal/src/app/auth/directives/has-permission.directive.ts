import { Directive, TemplateRef, ViewContainerRef, inject, input, computed, effect } from '@angular/core';
import { PermissionService } from '../services/permission.service';
import { PermissionMode } from '../models/permission-check.model';
import { normalizePermissionInput } from './permission-input.util';

/**
 * Structural directive — controls whether the host template exists in the DOM at all.
 * This is a UX convenience only; the backend re-validates every request regardless of
 * what this directive decided to render (see the architecture writeup, Section 1).
 *
 * Single permission:
 *   <button *appHasPermission="'epic.edit'">Edit</button>
 *
 * OR — default mode for a list ("has any of these"):
 *   <button *appHasPermission="['epic.edit', 'epic.admin']">Edit</button>
 *
 * AND — explicit:
 *   <div *appHasPermission="['epic.read','epic.export']; mode: 'all'">…</div>
 *
 * NONE — "show this only if the user holds none of these":
 *   <div *appHasPermission="['epic.edit']; mode: 'none'">Read-only mode.</div>
 *
 * Fallback content instead of nothing:
 *   <div *appHasPermission="'workflow.run'; else noAccess">…</div>
 *   <ng-template #noAccess>You don't have permission to run pipelines.</ng-template>
 */
@Directive({
  selector: '[appHasPermission]',
  standalone: true,
})
export class HasPermissionDirective {
  private readonly templateRef   = inject(TemplateRef<unknown>);
  private readonly viewContainer = inject(ViewContainerRef);
  private readonly permissionSvc = inject(PermissionService);

  readonly appHasPermission     = input.required<string | readonly string[]>();
  readonly appHasPermissionMode = input<PermissionMode>('any');
  readonly appHasPermissionElse = input<TemplateRef<unknown> | null>(null);

  /** Fails closed while permissions aren't ready yet (see PermissionService.isReady) — render
   *  nothing rather than briefly flash content the user may not be entitled to. */
  private readonly satisfied = computed(() => {
    if (!this.permissionSvc.isReady()) return false;
    const codes = normalizePermissionInput(this.appHasPermission());
    return this.permissionSvc.matches(this.appHasPermissionMode(), codes);
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
      const elseTemplate = this.appHasPermissionElse();

      this.viewContainer.clear();

      if (show) {
        this.viewContainer.createEmbeddedView(this.templateRef);
      } else if (elseTemplate) {
        this.viewContainer.createEmbeddedView(elseTemplate);
      }
    });
  }
}
