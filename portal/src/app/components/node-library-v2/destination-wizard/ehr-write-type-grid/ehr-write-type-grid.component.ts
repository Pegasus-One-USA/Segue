import { Component, computed, input, output } from '@angular/core';
import { EhrWriteTypeRow } from './ehr-write-type-grid.model';

/**
 * The EHR write-back destination's "Resource types" grid. Shows the types its source reads; those the target EHR does
 * not accept (or cannot take from this source) are greyed with the reason and cannot be ticked. A greyed type that
 * is somehow already selected (a destination saved before the rule) can still be unticked. Rows come from
 * classifyEhrWriteTypes, so the wizard's own offered list and recommendations agree with what is shown here.
 */
@Component({
  selector: 'app-ehr-write-type-grid',
  standalone: true,
  templateUrl: './ehr-write-type-grid.component.html',
  styleUrl: './ehr-write-type-grid.component.scss',
})
export class EhrWriteTypeGridComponent {
  readonly rows = input<EhrWriteTypeRow[]>([]);
  readonly selected = input<string[]>([]);
  /** The wizard's own Step 2 search text; rows not matching it are hidden. */
  readonly query = input<string>('');
  readonly toggled = output<string>();

  protected readonly visibleRows = computed(() => {
    const q = this.query().trim().toLowerCase();
    return q ? this.rows().filter((r) => r.resourceType.toLowerCase().includes(q)) : this.rows();
  });

  protected readonly greyedCount = computed(() => this.rows().filter((r) => !r.selectable).length);

  protected isSelected(type: string): boolean {
    return this.selected().includes(type);
  }

  /** Untickable only when greyed and not already selected. */
  protected isLocked(row: EhrWriteTypeRow): boolean {
    return !row.selectable && !this.isSelected(row.resourceType);
  }

  protected toggle(row: EhrWriteTypeRow): void {
    if (this.isLocked(row)) return;
    this.toggled.emit(row.resourceType);
  }
}
