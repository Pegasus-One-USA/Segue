import { Component, HostBinding, computed, inject, input, output, signal } from '@angular/core';
import { MappingRow, MappingInstanceSelection } from './field-mapping-model';
import { FmTreeNode, flattenLeaves } from './field-mapping-tree.util';
import { FieldMappingAnchorService } from './field-mapping-anchor.service';

interface NewMappingDraft {
  resource: string;
  tableName: string;
  mode: 'value' | 'childJson';
  sourcePaths: string[];
  childNodeId: string;
  targetName: string;
  instanceType: MappingInstanceSelection['type'];
}

function collectGroups(node: FmTreeNode, out: FmTreeNode[] = []): FmTreeNode[] {
  if (node.kind === 'group') {
    out.push(node);
    node.children.forEach(c => collectGroups(c, out));
  }
  return out;
}

/**
 * Collapsible bottom panel — a tabular view of every mapping AND the fully keyboard-operable
 * alternative to dragging: "+ Add mapping" builds a row from plain <select>s, no pointer input
 * required. Edits the exact same MappingRow[] the canvas renders (one state, two views).
 *
 * A resource can target more than one table (its primary table plus any added extra/child tables),
 * so the draft form has its own "Target table" picker, and every row/lookup key is
 * (resource, tableName, column) — matching the canvas's own row identity.
 */
@Component({
  selector: 'app-field-mapping-list',
  standalone: true,
  imports: [],
  templateUrl: './field-mapping-list.component.html',
  styleUrl: './field-mapping-list.component.scss',
})
export class FieldMappingListComponent {
  // Same injector subtree as FieldMappingCanvasComponent (which provides this) — reused here purely to
  // read the pan/zoom viewport's live height, so this panel's resize can be clamped against the real
  // total space it shares with the canvas, not a guessed constant.
  private readonly anchors = inject(FieldMappingAnchorService);

  readonly rows = input.required<MappingRow[]>();
  readonly resources = input.required<string[]>();
  readonly forest = input.required<FmTreeNode[]>();
  /** All tables (primary + extras) a resource currently targets — populates the draft's table picker. */
  readonly tablesForResource = input.required<(resource: string) => string[]>();
  readonly columnsFor = input.required<(resource: string, tableName: string) => string[]>();
  readonly targetByResource = input.required<Record<string, string>>();
  readonly isApproximated = input.required<(row: MappingRow) => boolean>();

  readonly addRow = output<MappingRow>();
  readonly removeRow = output<{ resource: string; tableName: string; targetName: string }>();
  readonly editRow = output<{ resource: string; tableName: string; targetName: string; invoker: HTMLElement }>();

  readonly collapsed = signal(false);
  readonly draft = signal<NewMappingDraft | null>(null);

  toggleCollapsed(): void { this.collapsed.update(v => !v); }

  // ── drag-to-resize this panel's whole height (handle + head + body) — the boundary between the
  // canvas above and this list. The canvas's own min-height (.fm-viewport CSS) MUST match
  // MIN_CANVAS_HEIGHT below, or the two clamps disagree and either overflow or leave a gap. ──
  private static readonly MIN_PANEL_HEIGHT = 150;
  private static readonly MIN_CANVAS_HEIGHT = 150;
  readonly panelHeight = signal(260);

  @HostBinding('style.height.px') get hostHeight(): number | null {
    return this.collapsed() ? null : this.panelHeight();
  }

  private resizeStart: { pointerId: number; startClientY: number; startHeight: number; totalAvailable: number } | null = null;

  onResizeHandlePointerDown(ev: PointerEvent): void {
    if (ev.button !== 0) return;
    // Captured once per drag, not read live on every move — the canvas viewport's own height changes
    // in lockstep with this panel's (both are flex siblings under one fixed-height parent), so re-reading
    // it mid-drag would just be reading back the effect of this same drag instead of the fixed total.
    this.resizeStart = {
      pointerId: ev.pointerId,
      startClientY: ev.clientY,
      startHeight: this.panelHeight(),
      totalAvailable: this.anchors.viewportSize().height + this.panelHeight(),
    };
    (ev.currentTarget as HTMLElement).setPointerCapture(ev.pointerId);
  }

  onResizeHandlePointerMove(ev: PointerEvent): void {
    if (!this.resizeStart || ev.pointerId !== this.resizeStart.pointerId) return;
    // Dragging the handle down grows the canvas above (and shrinks this list); dragging up grows this
    // list (and shrinks the canvas) — the handle sits at this panel's own top edge.
    const delta = ev.clientY - this.resizeStart.startClientY;
    const next = this.resizeStart.startHeight - delta;
    const maxHeight = this.resizeStart.totalAvailable - FieldMappingListComponent.MIN_CANVAS_HEIGHT;
    this.panelHeight.set(Math.max(
      FieldMappingListComponent.MIN_PANEL_HEIGHT,
      Math.min(maxHeight, next),
    ));
  }

  onResizeHandlePointerUp(ev: PointerEvent): void {
    if (!this.resizeStart || ev.pointerId !== this.resizeStart.pointerId) return;
    const el = ev.currentTarget as HTMLElement;
    if (el.hasPointerCapture(ev.pointerId)) el.releasePointerCapture(ev.pointerId);
    this.resizeStart = null;
  }

  rowKey(row: MappingRow): string { return `${row.resource}::${row.tableName}::${row.targetName}`; }

  sourceSummary(row: MappingRow): string {
    return row.mode === 'childJson'
      ? `${row.childNodeId} (whole node → JSON)`
      : row.sources.map(s => s.fhirPath).join(row.sources.length > 1 ? ` + ` : '');
  }

  modeLabel(row: MappingRow): string {
    if (row.mode === 'childJson') return 'Whole node → JSON';
    return row.sources.length > 1 ? `Joined ×${row.sources.length}` : 'Direct';
  }

  onRemove(row: MappingRow): void {
    this.removeRow.emit({ resource: row.resource, tableName: row.tableName, targetName: row.targetName });
  }

  onEdit(row: MappingRow, ev: MouseEvent): void {
    this.editRow.emit({
      resource: row.resource, tableName: row.tableName, targetName: row.targetName,
      invoker: ev.currentTarget as HTMLElement,
    });
  }

  // ── "+ Add mapping" draft form ───────────────────────────────────────────
  startDraft(): void {
    const resource = this.resources()[0] ?? '';
    const tableName = this.tablesForResource()(resource)[0] ?? '';
    this.draft.set({ resource, tableName, mode: 'value', sourcePaths: [''], childNodeId: '', targetName: '', instanceType: 'first' });
  }

  cancelDraft(): void { this.draft.set(null); }

  leavesFor(resource: string): FmTreeNode[] {
    const root = this.forest().find(r => r.resource === resource);
    return root ? flattenLeaves(root) : [];
  }

  groupsFor(resource: string): FmTreeNode[] {
    const root = this.forest().find(r => r.resource === resource);
    return root ? collectGroups(root) : [];
  }

  updateDraftResource(resource: string): void {
    const tableName = this.tablesForResource()(resource)[0] ?? '';
    this.draft.update(d => (d ? { ...d, resource, tableName, sourcePaths: [''], childNodeId: '', targetName: '' } : d));
  }

  updateDraftTable(tableName: string): void {
    this.draft.update(d => (d ? { ...d, tableName, targetName: '' } : d));
  }

  updateDraftMode(mode: 'value' | 'childJson'): void {
    this.draft.update(d => (d ? { ...d, mode } : d));
  }

  updateDraftSourcePath(i: number, path: string): void {
    this.draft.update(d => {
      if (!d) return d;
      const sourcePaths = [...d.sourcePaths];
      sourcePaths[i] = path;
      return { ...d, sourcePaths };
    });
  }

  addDraftSource(): void {
    this.draft.update(d => (d ? { ...d, sourcePaths: [...d.sourcePaths, ''] } : d));
  }

  removeDraftSource(i: number): void {
    this.draft.update(d => (d ? { ...d, sourcePaths: d.sourcePaths.filter((_, idx) => idx !== i) } : d));
  }

  updateDraftChildNode(id: string): void {
    this.draft.update(d => (d ? { ...d, childNodeId: id } : d));
  }

  updateDraftTarget(targetName: string): void {
    this.draft.update(d => (d ? { ...d, targetName } : d));
  }

  updateDraftInstanceType(instanceType: MappingInstanceSelection['type']): void {
    this.draft.update(d => (d ? { ...d, instanceType } : d));
  }

  readonly canSubmitDraft = computed(() => {
    const d = this.draft();
    if (!d || !d.resource || !d.tableName || !d.targetName) return false;
    return d.mode === 'childJson' ? !!d.childNodeId : d.sourcePaths.every(p => !!p);
  });

  submitDraft(): void {
    const d = this.draft();
    if (!d || !this.canSubmitDraft()) return;

    if (d.mode === 'childJson') {
      this.addRow.emit({
        resource: d.resource, sources: [], mode: 'childJson', childNodeId: d.childNodeId,
        targetName: d.targetName, tableName: d.tableName,
      });
    } else {
      const leaves = this.leavesFor(d.resource);
      const sources = d.sourcePaths.map(path => {
        const leaf = leaves.find(l => l.id === path);
        return {
          fhirPath: path,
          label: leaf?.label ?? path,
          jsonPath: leaf?.field?.jsonPath,
          valueType: leaf?.field?.valueType,
          arrays: leaf?.field?.arrays,
        };
      });
      this.addRow.emit({
        resource: d.resource, sources, mode: 'value',
        delimiter: sources.length > 1 ? ', ' : undefined,
        instance: { type: d.instanceType },
        targetName: d.targetName, tableName: d.tableName,
      });
    }
    this.draft.set(null);
  }
}
