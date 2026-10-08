import { Component, computed, input, output } from '@angular/core';
import { CanvasNode } from '../../../models/node-v2.model';
import { MPI_IDENTIFIERS } from '../../../data/mpi-identifiers.data';
import { MPI_NODE_NAME, mpiIdentifierIds } from '../../../services/mpi-node.util';

/**
 * The Master Patient Index node — an optional step placed directly after a source (Source → MPI → …), at most
 * one per workflow. Clicking it opens the identifier picker; its `+` adds what follows, exactly like a source's.
 */
@Component({
  selector: 'app-mpi-node',
  standalone: true,
  imports: [],
  templateUrl: './mpi-node.component.html',
  styleUrl: './mpi-node.component.scss',
  host: { class: 'node', '[style.left.px]': 'node().x', '[style.top.px]': 'node().y' },
})
export class MpiNodeComponent {
  readonly node = input.required<CanvasNode>();
  /** Hides the delete/add-next buttons — set by canvas.component from the builder's canMutate(). */
  readonly readOnly = input(false);
  /** False when this node's pipeline already has every applicable next step — the `+` is
   *  hidden rather than opening an empty library (see ApplicabilityServiceV2.canAddNext). */
  readonly canAddNext = input<boolean>(true);
  readonly canDelete = input(true);

  readonly configure = output<string>();
  readonly delete    = output<string>();
  readonly addNext   = output<string>();
  readonly dragMove  = output<{ nodeId: string; x: number; y: number }>();
  readonly portStart = output<{ nodeId: string; fromX: number; fromY: number }>();
  readonly portMove  = output<{ clientX: number; clientY: number }>();
  readonly portEnd   = output<{ nodeId: string; clientX: number; clientY: number }>();

  protected readonly name = MPI_NODE_NAME;
  protected readonly total = MPI_IDENTIFIERS.length;
  protected readonly count = computed(() => mpiIdentifierIds(this.node()).length);

  protected readonly summary = computed(() => {
    const count = this.count();
    if (!count) return 'No identifiers chosen';
    return `${count} identifier${count === 1 ? '' : 's'} selected`;
  });

  protected readonly ariaLabel = computed(() =>
    `${this.name} node, ${this.summary().toLowerCase()}. Press Enter to choose identifiers.`);

  onKeyOpen(e: Event): void {
    e.preventDefault();
    this.configure.emit(this.node().id);
  }

  onAddNextClick(e: MouseEvent): void {
    e.stopPropagation();
    this.addNext.emit(this.node().id);
  }

  onDeleteClick(e: MouseEvent): void {
    e.stopPropagation();
    this.delete.emit(this.node().id);
  }

  // ── drag on circle (click without moving opens the picker) ────────────────
  private drag: { startClientX: number; startClientY: number; startNodeX: number; startNodeY: number } | null = null;
  private dragged = false;

  onCirclePointerDown(e: PointerEvent): void {
    if (e.button !== 0) return;
    e.stopPropagation();
    this.dragged = false;
    this.drag = {
      startClientX: e.clientX,
      startClientY: e.clientY,
      startNodeX: this.node().x,
      startNodeY: this.node().y,
    };
    (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
  }

  onCirclePointerMove(e: PointerEvent): void {
    if (!this.drag) return;
    const dx = e.clientX - this.drag.startClientX;
    const dy = e.clientY - this.drag.startClientY;
    if (Math.abs(dx) > 3 || Math.abs(dy) > 3) this.dragged = true;
    this.dragMove.emit({
      nodeId: this.node().id,
      x: Math.round(this.drag.startNodeX + dx),
      y: Math.round(this.drag.startNodeY + dy),
    });
  }

  onCirclePointerUp(e: PointerEvent): void {
    if (!this.drag) return;
    try { (e.currentTarget as HTMLElement).releasePointerCapture(e.pointerId); } catch { /* pointer already released */ }
    const wasDrag = this.dragged;
    this.drag = null; this.dragged = false;
    if (!wasDrag) this.configure.emit(this.node().id);
  }

  // ── port-out drag ─────────────────────────────────────────────────────────
  onPortPointerDown(e: PointerEvent): void {
    e.stopPropagation(); e.preventDefault();
    this.portStart.emit({ nodeId: this.node().id, fromX: this.node().x, fromY: this.node().y });
    const move = (ev: PointerEvent) => this.portMove.emit({ clientX: ev.clientX, clientY: ev.clientY });
    const up   = (ev: PointerEvent) => {
      document.removeEventListener('pointermove', move);
      document.removeEventListener('pointerup', up);
      this.portEnd.emit({ nodeId: this.node().id, clientX: ev.clientX, clientY: ev.clientY });
    };
    document.addEventListener('pointermove', move);
    document.addEventListener('pointerup', up);
  }
}
