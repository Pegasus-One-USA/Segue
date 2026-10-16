import { Component, computed, input } from '@angular/core';
import { FormArray, ReactiveFormsModule } from '@angular/forms';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { CdsFixMappingField, CdsFixTypeDescriptor } from '../../models/cds.models';
import { CDS_FIX_MAPPINGS_MAX, CdsMappingGroup, makeCdsMappingGroup } from '../../models/cds-service-options.util';

/**
 * The code-mapping table of one fix type: what the fix adds in the EHR (a preference-list item, an RxNorm or ICD-10
 * code...), optionally per trigger (a drug class, a care gap). The columns come from the check's fix descriptor.
 */
@Component({
  selector: 'app-cds-fix-mapping-table',
  standalone: true,
  imports: [ReactiveFormsModule, MatIconModule, MatTooltipModule],
  templateUrl: './cds-fix-mapping-table.component.html',
  styleUrl: './cds-fix-mapping-table.component.scss',
})
export class CdsFixMappingTableComponent {
  readonly descriptor = input.required<CdsFixTypeDescriptor>();
  readonly mappings = input.required<FormArray<CdsMappingGroup>>();
  /** Set after a save attempt, so untouched blanks show their errors too. */
  readonly showAllErrors = input(false);
  /** The fix is on but has no mapping yet, and it can't be offered without one. */
  readonly needsMapping = input(false);

  readonly max = CDS_FIX_MAPPINGS_MAX;

  /** Trigger is shown only when the fix has trigger options; otherwise every mapping is for "any". */
  readonly fields = computed<CdsFixMappingField[]>(() =>
    this.descriptor().mappingFields.filter(f => f.key !== 'trigger' || !!this.descriptor().triggers?.length));

  readonly idPrefix = computed(() => `cds-fix-${this.descriptor().key}`);

  add(): void {
    const array = this.mappings();
    if (array.disabled || array.length >= this.max) return;
    const usedTriggers = new Set(array.getRawValue().map(m => m.trigger));
    // Start a new row on the first trigger nobody mapped yet, so adding rows doesn't create duplicates.
    const free = this.descriptor().triggers?.find(t => !usedTriggers.has(t.value))?.value;
    const row = makeCdsMappingGroup(this.descriptor());
    if (free && array.length > 0) row.controls.trigger.setValue(free);
    array.push(row);
    array.markAsDirty();
  }

  remove(index: number): void {
    const array = this.mappings();
    if (array.disabled) return;
    array.removeAt(index);
    array.markAsDirty();
  }

  showError(row: CdsMappingGroup, key: string): boolean {
    const control = row.get(key);
    return !!control && control.invalid && (control.touched || this.showAllErrors());
  }

  duplicateTrigger(): string | null {
    const value = this.mappings().getError('duplicateTrigger') as string | undefined;
    if (!value) return null;
    if (value === 'any') return 'any';
    return this.descriptor().triggers?.find(t => t.value === value)?.label ?? value;
  }
}
