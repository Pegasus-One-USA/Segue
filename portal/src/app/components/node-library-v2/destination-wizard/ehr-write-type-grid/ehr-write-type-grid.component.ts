import { Component, computed, input, output } from '@angular/core';
import { EhrWriteTypeRow } from './ehr-write-type-grid.model';
import { EhrWriteKindSwitches, NO_KIND_SWITCHES, isKindOn, sharedSwitchNote } from './ehr-write-kinds.model';
import { EhrWriteKindListComponent } from './ehr-write-kind-list.component';

/** A kind ticked or unticked under a type. */
export interface EhrWriteKindToggle {
  resourceType: string;
  kindId: string;
}

/**
 * The EHR write-back destination's "Resource types" grid. Shows the types its source reads; those the target EHR does
 * not accept (or cannot take from this source) are greyed with the reason and cannot be ticked. A greyed type that
 * is somehow already selected (a destination saved before the rule) can still be unticked. A type the EHR writes in
 * more than one way lists its kinds under it (EhrWriteKindListComponent). Rows come from classifyEhrWriteTypes, so
 * the wizard's own offered list and recommendations agree with what is shown here.
 */
@Component({
  selector: 'app-ehr-write-type-grid',
  standalone: true,
  imports: [EhrWriteKindListComponent],
  templateUrl: './ehr-write-type-grid.component.html',
  styleUrl: './ehr-write-type-grid.component.scss',
})
export class EhrWriteTypeGridComponent {
  readonly rows = input<EhrWriteTypeRow[]>([]);
  readonly selected = input<string[]>([]);
  /** The wizard's own Step 2 search text; rows not matching it are hidden. */
  readonly query = input<string>('');
  /** The destination's saved switches (dest_enabledVariants, dest_createHolderEncounter): which kinds are on. */
  readonly switches = input<EhrWriteKindSwitches>(NO_KIND_SWITCHES);
  readonly toggled = output<string>();
  readonly kindToggled = output<EhrWriteKindToggle>();

  protected readonly visibleRows = computed(() => {
    const q = this.query().trim().toLowerCase();
    return q ? this.rows().filter((r) => r.resourceType.toLowerCase().includes(q)) : this.rows();
  });

  protected readonly greyedCount = computed(() => this.rows().filter((r) => !r.selectable).length);
  protected readonly anyKinds = computed(() => this.rows().some((r) => r.kinds.length > 0));

  /** Per type: the ids of the kinds written (none while the type is not ticked). */
  protected readonly chosenKinds = computed(() => {
    const switches = this.switches();
    const selected = this.selected();
    return Object.fromEntries(this.rows().map((r) => [
      r.resourceType,
      selected.includes(r.resourceType) ? r.kinds.filter((k) => isKindOn(k, switches)).map((k) => k.id) : [],
    ]));
  });

  /** Per type, per kind id: what its shared switch does to the other ticked types (sharedSwitchNote). */
  protected readonly sharedNotes = computed(() => {
    const rows = this.rows();
    const selected = this.selected();
    const switches = this.switches();
    return Object.fromEntries(rows.map((r) => [
      r.resourceType,
      Object.fromEntries(r.kinds.map((k) => [k.id, sharedSwitchNote(k, rows, selected, switches)]).filter(([, note]) => note)),
    ]));
  });

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

  protected toggleKind(row: EhrWriteTypeRow, kindId: string): void {
    this.kindToggled.emit({ resourceType: row.resourceType, kindId });
  }
}
