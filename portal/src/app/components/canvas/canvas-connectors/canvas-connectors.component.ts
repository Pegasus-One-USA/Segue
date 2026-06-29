import { Component, input, computed } from '@angular/core';
import { CanvasNode } from '../../../models/node.model';
import { CanvasEdge } from '../../../models/edge.model';

interface ConnectorPath {
  id: string;
  d: string;
}

@Component({
  selector: 'app-canvas-connectors',
  standalone: true,
  imports: [],
  templateUrl: './canvas-connectors.component.html',
  styleUrl: './canvas-connectors.component.scss',
})
export class CanvasConnectorsComponent {
  readonly nodes       = input<CanvasNode[]>([]);
  readonly edges       = input<CanvasEdge[]>([]);
  readonly tempPath    = input<string | null>(null);

  private readonly NODE_R = 60;

  private nodeMap = () => {
    const m = new Map<string, CanvasNode>();
    this.nodes().forEach(n => m.set(n.id, n));
    return m;
  };

  readonly paths = computed<ConnectorPath[]>(() => {
    const map = this.nodeMap();
    return this.edges()
      .map(e => {
        const a = map.get(e.from);
        const b = map.get(e.to);
        if (!a || !b) return null;
        return { id: e.id, d: this.edgePath(a, b) };
      })
      .filter((p): p is ConnectorPath => p !== null);
  });

  private edgePath(a: CanvasNode, b: CanvasNode): string {
    const sx = a.x + this.NODE_R, sy = a.y;
    const ex = b.x - this.NODE_R, ey = b.y;
    const mx = (sx + ex) / 2;
    return `M ${sx} ${sy} C ${mx} ${sy}, ${mx} ${ey}, ${ex} ${ey}`;
  }
}
