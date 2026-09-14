import { Component, input, output, viewChild } from '@angular/core';
import { EhrVendorSourceFormComponent } from '../../shared-v2/ehr-vendor-source-form/ehr-vendor-source-form.component';
import { SourceConfigFormComponent } from '../../shared-v2/config-form/config-form-v2.contract';

/**
 * Thin, independently-editable wrapper around the shared EhrVendorSourceFormComponent engine, fixed to the Healow
 * vendor. Forwards every input/output the engine exposes unchanged — the only thing this file owns is "which
 * vendor" (see the `[vendor]="'Healow'"` binding in the template) and its display brand name (`vendorLabel`,
 * 'eCW' — eClinicalWorks markets this product as eCW; 'Healow' is only the backend SourceSystemType enum member
 * name and must stay as-is for backend matching). Registered in source-form.registry.ts under the `healow` key
 * from sources.data.ts.
 */
@Component({
  selector: 'app-healow-source-form',
  standalone: true,
  imports: [EhrVendorSourceFormComponent],
  templateUrl: './healow-source-form.component.html',
  styleUrl: './healow-source-form.component.scss',
})
export class HealowSourceFormComponent implements SourceConfigFormComponent {
  readonly isMaximized = input<boolean>(false);

  readonly cancelled = output<void>();
  readonly saved     = output<string | null>();
  readonly closeAll  = output<void>();
  readonly toggleMaximizeRequest = output<void>();

  private readonly engine = viewChild(EhrVendorSourceFormComponent);

  getFields(): Record<string, string> | null {
    return this.engine()?.getFields() ?? null;
  }

  /** Passthrough so a host (node-library-dialog's onOverlayClosed) can check whether this form has
   *  been edited before prompting "Discard changes?" on Escape/backdrop-click, the same way its own
   *  Cancel button already does via EhrVendorSourceFormComponent.cancel(). */
  hasUnsavedChanges(): boolean {
    return this.engine()?.hasUnsavedChanges() ?? false;
  }
}
