import { Component, input, output } from '@angular/core';
import { NodeToolsComponent } from '../node-tools/node-tools.component';
import { CanvasNode } from '../../../models/node.model';
import { TRANSFORMS } from '../../../data/transforms.data';

@Component({
  selector: 'app-transform-node',
  standalone: true,
  imports: [NodeToolsComponent],
  templateUrl: './transform-node.component.html',
  styleUrl: './transform-node.component.scss',
  host: { class: 'node', '[style.left.px]': 'node().x', '[style.top.px]': 'node().y' },
})
export class TransformNodeComponent {
  readonly node        = input.required<CanvasNode>();
  readonly plusEnabled = input(true);

  readonly addNext  = output<string>();
  readonly delete   = output<string>();
  readonly dragMove = output<{ nodeId: string; x: number; y: number }>();
  readonly dragEnd  = output<{ nodeId: string; x: number; y: number }>();
  readonly portStart = output<{ nodeId: string; fromX: number; fromY: number }>();
  readonly portMove  = output<{ clientX: number; clientY: number }>();
  readonly portEnd   = output<{ nodeId: string; clientX: number; clientY: number }>();

  protected transformName(): string {
    if (this.node().kind !== 'transform') return '';
    const t = TRANSFORMS.find(x => x.id === (this.node() as any).transformId);
    return t?.name ?? (this.node() as any).transformId ?? '';
  }

  protected sourceName(): string {
    return (this.node() as any).sourceName ?? 'Epic';
  }

  protected isCaveat(): boolean {
    return (this.node() as any).statusAtAdd === 'caveat';
  }

  private drag: { sX: number; sY: number; nX: number; nY: number } | null = null;

  onCirclePointerDown(e: PointerEvent): void {
    if (e.button !== 0) return; e.stopPropagation();
    this.drag = { sX: e.clientX, sY: e.clientY, nX: this.node().x, nY: this.node().y };
    (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
  }

  onCirclePointerMove(e: PointerEvent): void {
    if (!this.drag) return;
    this.dragMove.emit({ nodeId: this.node().id, x: Math.round(this.drag.nX + e.clientX - this.drag.sX), y: Math.round(this.drag.nY + e.clientY - this.drag.sY) });
  }

  onCirclePointerUp(e: PointerEvent): void {
    if (!this.drag) return;
    try { (e.currentTarget as HTMLElement).releasePointerCapture(e.pointerId); } catch {}
    this.drag = null;
  }

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
