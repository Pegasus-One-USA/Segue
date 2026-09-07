import { Component, computed, input, output } from '@angular/core';
import { FmTreeNode } from '../field-mapping-tree.util';

/**
 * One recursive row in the FHIR payload tree — a group header (foldable) or a leaf field.
 *
 * A deliberate copy of FieldMappingTreeNodeComponent's LOOK with none of its canvas machinery: no
 * pointer-capture drag, no FieldMappingAnchorService registration (nothing draws wires to these rows),
 * no arm/keyboard-map gesture. For a FHIR destination there is no target card to drag onto — a row is
 * clicked to attach a transformation rule to that path, so a plain click is the whole interaction and
 * a group header's click still means expand/collapse.
 */
@Component({
  selector: 'app-fhir-payload-tree-node',
  standalone: true,
  imports: [FhirPayloadTreeNodeComponent],
  templateUrl: './fhir-payload-tree-node.component.html',
  styleUrl: './fhir-payload-tree-node.component.scss',
})
export class FhirPayloadTreeNodeComponent {
  readonly node = input.required<FmTreeNode>();
  readonly depth = input<number>(0);
  /** This row's position among its own siblings — drives zebra striping, which `:nth-child` can't do
   *  here because recursion wraps every row in its own host element. */
  readonly siblingIndex = input<number>(0);
  readonly isCollapsed = input.required<(id: string) => boolean>();
  /** Whether this path already carries a rule — the visual equivalent of the SQL tree's "mapped" state. */
  readonly hasRule = input.required<(id: string) => boolean>();

  readonly toggleCollapse = output<string>();
  /** A row was clicked: open the transformation-rule modal for this path. Emitted for leaves only —
   *  a group header's click is its accordion, a long-established gesture this must not hijack. */
  readonly fieldClick = output<FmTreeNode>();

  readonly isGroup = computed(() => this.node().kind === 'group');
  readonly isStripe = computed(() => this.siblingIndex() % 2 === 1);

  /** Leaf labels read as "Address › Period › End" so a nested path is legible without the indentation
   *  alone having to carry it — matching the payload card in the mapping canvas. */
  readonly label = computed(() => this.node().label);

  readonly referenceTargetTypes = computed(() => this.node().field?.referenceTargetTypes ?? []);

  readonly fieldCount = computed(() => countLeaves(this.node()));

  readonly ariaLabel = computed(() => {
    const node = this.node();
    if (node.kind === 'group') {
      return `${node.label}, ${this.fieldCount()} fields${node.isArray ? ', repeats per record' : ''}`;
    }
    return `${node.label}${this.hasRule()(node.id) ? ', has a rule' : ''}`;
  });

  readonly leafTooltip = computed(() => {
    const node = this.node();
    const type = node.field?.valueType ? ` (${node.field.valueType})` : '';
    return `${node.id}${type} — click to attach a transformation rule`;
  });

  onHeaderClick(): void {
    this.toggleCollapse.emit(this.node().id);
  }

  onLeafClick(): void {
    this.fieldClick.emit(this.node());
  }

  onKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Enter' && event.key !== ' ') return;
    event.preventDefault();
    if (this.isGroup()) {
      this.onHeaderClick();
      return;
    }
    this.onLeafClick();
  }
}

function countLeaves(node: FmTreeNode): number {
  if (node.kind === 'leaf') return 1;
  return node.children.reduce((total, child) => total + countLeaves(child), 0);
}
