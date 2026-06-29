import { Component, input, output, ElementRef, inject, OnInit, OnDestroy } from '@angular/core';
import { NodeToolsComponent } from '../node-tools/node-tools.component';
import { CanvasNode } from '../../../models/node.model';
import { INGESTION_MODES } from '../../../data/ingestion-modes.data';

export interface DragEvent { nodeId: string; x: number; y: number; }
export interface PortConnectStart { nodeId: string; fromX: number; fromY: number; }

@Component({
  selector: 'app-source-node',
  standalone: true,
  imports: [NodeToolsComponent],
  templateUrl: './source-node.component.html',
  styleUrl: './source-node.component.scss',
  host: { class: 'node', '[style.left.px]': 'node().x', '[style.top.px]': 'node().y' },
})
export class SourceNodeComponent {
  readonly node        = input.required<CanvasNode>();
  readonly plusEnabled = input(true);

  readonly configure   = output<string>();
  readonly addNext     = output<string>();
  readonly delete      = output<string>();
  readonly dragMove    = output<DragEvent>();
  readonly dragEnd     = output<DragEvent>();
  readonly portStart   = output<PortConnectStart>();
  readonly portEnd     = output<{ nodeId: string; clientX: number; clientY: number }>();
  readonly portMove    = output<{ clientX: number; clientY: number }>();

  protected modeName(): string {
    const m = this.node().fields?.['Ingestion mode'];
    return INGESTION_MODES.find(x => x.id === m)?.name ?? '—';
  }

  protected envLabel(): string {
    return this.node().fields?.['Environment'] === 'production' ? 'PROD' : 'SBX';
  }

  protected envColor(): string {
    return this.node().fields?.['Environment'] === 'production' ? '#b42318' : '#1d8cf8';
  }

  protected nodeColor(): string {
    return (this.node() as any).color ?? 'var(--epic)';
  }

  protected abbr(): string {
    return (this.node() as any).abbr ?? 'EP';
  }

  protected name(): string {
    return this.node().fields?.['__name'] ?? 'Epic';
  }

  protected context(): string {
    return this.node().fields?.['App context'] ?? '';
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
    try { (e.currentTarget as HTMLElement).releasePointerCapture(e.pointerId); } catch {}
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
