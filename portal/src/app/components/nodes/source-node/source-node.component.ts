import { Component, input, output } from '@angular/core';
import { CanvasNode, SourceNode } from '../../../models/node.model';

export interface DragEvent { nodeId: string; x: number; y: number; }
export interface PortConnectStart { nodeId: string; fromX: number; fromY: number; }

@Component({
  selector: 'app-source-node',
  standalone: true,
  imports: [],
  templateUrl: './source-node.component.html',
  styleUrl: './source-node.component.scss',
  host: { class: 'node', '[style.left.px]': 'node().x', '[style.top.px]': 'node().y' },
})
export class SourceNodeComponent {
  readonly node = input.required<CanvasNode>();

  readonly configure = output<string>();
  readonly delete    = output<string>();
  readonly addNext   = output<string>();
  readonly dragMove    = output<DragEvent>();
  readonly dragEnd     = output<DragEvent>();
  readonly portStart   = output<PortConnectStart>();
  readonly portEnd     = output<{ nodeId: string; clientX: number; clientY: number }>();
  readonly portMove    = output<{ clientX: number; clientY: number }>();

  protected envLabel(): string {
    return this.node().fields?.['Environment'] === 'production' ? 'PROD' : 'SBX';
  }

  protected envColor(): string {
    return this.node().fields?.['Environment'] === 'production' ? '#b42318' : '#1d8cf8';
  }

  protected nodeColor(): string {
    return (this.node() as SourceNode).color ?? 'var(--epic)';
  }

  protected abbr(): string {
    return (this.node() as SourceNode).abbr ?? 'EP';
  }

  protected name(): string {
    return this.node().fields?.['__name'] ?? 'Epic';
  }

  protected context(): string {
    return this.node().fields?.['App context'] ?? '';
  }

  protected contextLabel(): string {
    const key = this.node().fields?.['Epic audience'] ?? this.node().fields?.['App key'] ?? '';
    const map: Record<string, string> = {
      'provider-ehr-launch': 'Provider EHR Launch',
      'provider-standalone': 'Provider Standalone',
      'backend-system':      'Backend Systems',
      'patient-standalone':  'Patient',
      'patient':             'Patient',
    };
    return map[key] ?? (this.context() || 'FHIR Source');
  }

  onAddNextClick(e: MouseEvent): void {
    e.stopPropagation();
    this.addNext.emit(this.node().id);
  }

  onDeleteClick(e: MouseEvent): void {
    e.stopPropagation();
    this.delete.emit(this.node().id);
  }

  // ── drag on circle ────────────────────────────────────────────────────────
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
