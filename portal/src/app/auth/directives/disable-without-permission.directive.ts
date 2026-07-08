import { Directive, inject, input, computed } from '@angular/core';
import { PermissionService } from '../services/permission.service';
import { PermissionMode } from '../models/permission-check.model';
import { normalizePermissionInput } from './permission-input.util';

/**
 * Attribute directive — keeps the host element in the DOM and visible, but toggles
 * `disabled` (and mirrors it to `aria-disabled`, since `disabled` alone isn't reliably
 * announced by screen readers on non-form elements) when the permission isn't held.
 *
 * Uses a declarative host binding rather than an imperative Renderer2 write on purpose:
 * Angular Material controls (mat-button, mat-icon-button, etc.) manage their own
 * `disabled` state via their own host bindings, which get re-asserted on every change
 * detection cycle. An imperative one-time `Renderer2.setProperty` loses that race — it
 * gets silently overwritten by Material's binding on the very next tick. Declaring
 * `disabled` here as a host binding puts this directive on equal footing, resolved by
 * Angular's normal binding reconciliation (last-registered directive wins) instead of
 * an unpredictable imperative-vs-declarative race.
 *
 * Use this instead of *appHasPermission when the user should SEE the control exists
 * (so they understand the feature is there) but can't use it — e.g. a Save button on
 * a form they can view but not edit. Pair with a reason so a disabled control isn't a
 * silent dead end:
 *
 *   <button [appDisableWithoutPermission]="'epic.edit'"
 *           appDisableWithoutPermissionReason="You need Epic Edit access to save changes.">
 *     Save
 *   </button>
 */
@Directive({
  selector: '[appDisableWithoutPermission]',
  standalone: true,
  host: {
    '[disabled]': 'isDisabled()',
    '[attr.aria-disabled]': 'isDisabled()',
    '[attr.title]': 'title()',
  },
})
export class DisableWithoutPermissionDirective {
  private readonly permissionSvc = inject(PermissionService);

  readonly appDisableWithoutPermission       = input.required<string | readonly string[]>();
  readonly appDisableWithoutPermissionMode   = input<PermissionMode>('any');
  readonly appDisableWithoutPermissionReason = input<string>('');

  private readonly allowed = computed(() => {
    if (!this.permissionSvc.isReady()) return false;
    const codes = normalizePermissionInput(this.appDisableWithoutPermission());
    return this.permissionSvc.matches(this.appDisableWithoutPermissionMode(), codes);
  });

  protected readonly isDisabled = computed(() => !this.allowed());

  protected readonly title = computed(() =>
    this.isDisabled() && this.appDisableWithoutPermissionReason()
      ? this.appDisableWithoutPermissionReason()
      : null
  );
}
