import { Injectable, signal, computed } from '@angular/core';

export const SOURCE_RANK = 0;
export const NODE_RADIUS = 60;

@Injectable({ providedIn: 'root' })
export class CanvasService {
  private readonly _pan  = signal<{ x: number; y: number }>({ x: 0, y: 0 });
  private readonly _zoom = signal<number>(1);

  readonly pan  = this._pan.asReadonly();
  readonly zoom = this._zoom.asReadonly();

  readonly zoomPercent = computed(() => Math.round(this._zoom() * 100) + '%');

  readonly transformStyle = computed(() => {
    const { x, y } = this._pan();
    const z = this._zoom();
    return `translate(${x}px,${y}px) scale(${z})`;
  });

  setZoom(z: number, cx?: number, cy?: number): void {
    const clamped = Math.min(1.6, Math.max(0.4, z));
    const { x, y } = this._pan();
    const curZoom = this._zoom();
    const wx = (cx ?? 0 - x) / curZoom;
    const wy = (cy ?? 0 - y) / curZoom;
    this._zoom.set(clamped);
    if (cx !== undefined && cy !== undefined) {
      this._pan.set({ x: cx - wx * clamped, y: cy - wy * clamped });
    }
  }

  setPan(x: number, y: number): void {
    this._pan.set({ x, y });
  }

  movePan(dx: number, dy: number): void {
    const { x, y } = this._pan();
    this._pan.set({ x: x + dx, y: y + dy });
  }

  reset(): void {
    this._pan.set({ x: 0, y: 0 });
    this._zoom.set(1);
  }

  clientToFlow(clientX: number, clientY: number, shellRect: DOMRect): { x: number; y: number } {
    const { x, y } = this._pan();
    const z = this._zoom();
    return {
      x: (clientX - shellRect.left - x) / z,
      y: (clientY - shellRect.top  - y) / z,
    };
  }

  edgePath(
    from: { x: number; y: number },
    to:   { x: number; y: number }
  ): string {
    const sx = from.x + NODE_RADIUS;
    const sy = from.y;
    const ex = to.x   - NODE_RADIUS;
    const ey = to.y;
    const mx = (sx + ex) / 2;
    return `M ${sx} ${sy} C ${mx} ${sy}, ${mx} ${ey}, ${ex} ${ey}`;
  }
}
