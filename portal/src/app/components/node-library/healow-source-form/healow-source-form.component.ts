import { Component, input, output, viewChild } from '@angular/core';
import { EhrVendorSourceFormComponent } from '../../shared/ehr-vendor-source-form/ehr-vendor-source-form.component';
import { SourceConfigFormComponent } from '../../shared/config-form/config-form.contract';

/**
 * Thin, independently-editable wrapper around the shared EhrVendorSourceFormComponent engine, fixed to the Healow
 * vendor. Forwards every input/output the engine exposes unchanged — the only thing this file owns is "which
 * vendor" (see the `[vendor]="'Healow'"` binding in the template). Registered in source-form.registry.ts under the
 * `healow` key from sources.data.ts.
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
  readonly saved     = output<void>();
  readonly closeAll  = output<void>();
  readonly toggleMaximizeRequest = output<void>();

  private readonly engine = viewChild(EhrVendorSourceFormComponent);

  getFields(): Record<string, string> | null {
    return this.engine()?.getFields() ?? null;
  }
}
