import { Component, input } from '@angular/core';
import { ReactiveFormsModule } from '@angular/forms';
import { MatIconModule } from '@angular/material/icon';
import { CdsFixTypeDescriptor } from '../../models/cds.models';
import { CdsFixTypeGroup, CdsFixesGroup, cdsFixTypeNeedsMapping, cdsFixesNoTypeOn } from '../../models/cds-service-options.util';
import { cdsFixActionLabel } from '../../models/cds-labels';
import { CdsFixMappingTableComponent } from '../cds-fix-mapping-table/cds-fix-mapping-table.component';

/**
 * One-click fixes for a service: the master switch (off by default), a switch per fix type the check offers, each
 * type's code mappings, and which fix is pre-selected. The page asks before saving fixes switched on.
 */
@Component({
  selector: 'app-cds-fixes-editor',
  standalone: true,
  imports: [ReactiveFormsModule, MatIconModule, CdsFixMappingTableComponent],
  templateUrl: './cds-fixes-editor.component.html',
  styleUrl: './cds-fixes-editor.component.scss',
})
export class CdsFixesEditorComponent {
  /** In the same order as the group's fix types (the group is built from them). */
  readonly descriptors = input.required<CdsFixTypeDescriptor[]>();
  readonly group = input.required<CdsFixesGroup>();
  readonly showAllErrors = input(false);

  readonly actionLabel = cdsFixActionLabel;

  descriptorAt(index: number): CdsFixTypeDescriptor | undefined {
    return this.descriptors()[index];
  }

  /** Fixes are on but no fix type is ticked: shown once the admin tries to save. */
  noTypeOn(): boolean {
    return this.showAllErrors() && cdsFixesNoTypeOn(this.group());
  }

  needsMapping(index: number, type: CdsFixTypeGroup): boolean {
    return cdsFixTypeNeedsMapping(this.descriptorAt(index), type) && (this.showAllErrors() || type.controls.enabled.dirty);
  }

  /** The fix types switched on, for the "pre-select" choice. */
  enabledTypes(): CdsFixTypeDescriptor[] {
    const types = this.group().controls.fixTypes.controls;
    return this.descriptors().filter((_, i) => types[i]?.controls.enabled.value);
  }

  /** Keeps the pre-selected fix valid when its type is switched off. */
  onTypeToggled(type: CdsFixTypeGroup): void {
    const recommended = this.group().controls.recommendedFixType;
    if (!type.controls.enabled.value && recommended.value === type.controls.key.value) recommended.setValue('');
  }
}
