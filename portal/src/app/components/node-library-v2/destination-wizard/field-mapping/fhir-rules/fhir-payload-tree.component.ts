import { Component, computed, input, output, signal } from '@angular/core';
import { FmTreeNode, filterForest, flattenLeaves } from '../field-mapping-tree.util';
import { FhirPayloadTreeNodeComponent } from './fhir-payload-tree-node.component';

/**
 * The FHIR payload card: the same "PAYLOAD {resource}" tree the mapping canvas shows, so a FHIR
 * destination reads identically to a SQL one on the source side.
 *
 * A deliberate copy of FieldMappingSourceTreeComponent rather than a mode on it, with the canvas-only
 * parts removed: no absolute x/y placement, no header drag, no resize/anchor refresh, no wire ports to
 * register. It is docked inside the rules panel, and a row click means "attach a transformation rule to
 * this path" instead of "start dragging a wire to a destination column" — for a FHIR destination there
 * is no column card to drag onto.
 */
@Component({
  selector: 'app-fhir-payload-tree',
  standalone: true,
  imports: [FhirPayloadTreeNodeComponent],
  templateUrl: './fhir-payload-tree.component.html',
  styleUrl: './fhir-payload-tree.component.scss',
})
export class FhirPayloadTreeComponent {
  readonly forest = input.required<FmTreeNode[]>();
  readonly isCollapsed = input.required<(id: string) => boolean>();
  readonly hasRule = input.required<(id: string) => boolean>();

  readonly toggleCollapse = output<string>();
  readonly fieldClick = output<FmTreeNode>();

  // ── search ────────────────────────────────────────────────────────────────
  // Filters this card's own forest client-side — the whole forest is already in memory, so there is no
  // round trip. Kept local: the panel's collapse-state map is only consulted while NOT searching (see
  // effectiveIsCollapsed), so it never has to know about this.
  readonly searchQuery = signal('');
  readonly isSearching = computed(() => this.searchQuery().trim().length > 0);
  readonly effectiveQuery = computed(() => this.searchQuery().trim());
  readonly hasActiveFilter = computed(() => this.effectiveQuery().length > 0);

  readonly filteredRoots = computed(() => {
    const original = this.forest();
    return filterForest(original, this.effectiveQuery())
      .map(node => ({ node, colorIndex: original.findIndex(root => root.id === node.id) }));
  });

  readonly matchCount = computed(() =>
    this.hasActiveFilter()
      ? this.filteredRoots().reduce((sum, root) => sum + flattenLeaves(root.node).length, 0)
      : 0);

  /** While searching, force every surviving node open so matches are actually visible — the panel's own
   *  fold state only applies when there is no active query. */
  readonly effectiveIsCollapsed = computed(() => {
    if (!this.hasActiveFilter()) return this.isCollapsed();
    return () => false;
  });

  /** Header label: the first resource, plus a "+N" when several are in the same card. */
  readonly headerName = computed(() => {
    const forest = this.forest();
    const first = forest[0]?.label ?? 'payload';
    return forest.length > 1 ? `${first} + ${forest.length - 1}` : first;
  });

  /** Capped-and-scrollable by default, so a large resource (Patient's ~180 fields) doesn't push the
   *  rules list off screen. Toggled to full height by the header's own expand button. */
  readonly fitMode = signal(true);

  groupColorVar(index: number): string {
    // Same rank palette the mapping canvas tints its resource groups with, so a resource keeps its
    // colour between the two screens.
    return `var(--fm-rank-${(index % 6) + 1})`;
  }

  onSearchInput(value: string): void {
    this.searchQuery.set(value);
  }

  clearSearch(): void {
    this.searchQuery.set('');
  }

  toggleFit(): void {
    this.fitMode.update(fit => !fit);
  }
}
