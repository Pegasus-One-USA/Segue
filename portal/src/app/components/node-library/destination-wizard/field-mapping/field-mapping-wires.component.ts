import { Component, computed, inject, input, output } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { MappingRow } from './field-mapping-model';
import { MappingSuggestion } from './field-mapping-automap.util';
import { FieldMappingAnchorService } from './field-mapping-anchor.service';

export interface FmWirePath {
  rowKey: string;
  d: string;
  stroke: string;
  dashed: boolean;
}

export interface FmSuggestionPath {
  rowKey: string;
  d: string;
  confidence: number;
}

export interface FmTempWire {
  fromId: string;
  toClientX: number;
  toClientY: number;
  stroke: string;
}

/**
 * SVG overlay drawing one cubic-bezier wire per mapped source (same formula as the main workflow
 * canvas's canvas-connectors.component.ts: M sx,sy C mx,sy, mx,ey, ex,ey), plus the live temp wire
 * while dragging. Anchors come from FieldMappingAnchorService and fall back to the nearest registered
 * ancestor when the exact node is hidden under a collapsed group — mirroring how a wire should still
 * point at "Name" when "Name > Given" is folded.
 */
@Component({
  selector: 'app-field-mapping-wires',
  standalone: true,
  imports: [DecimalPipe],
  templateUrl: './field-mapping-wires.component.html',
  styleUrl: './field-mapping-wires.component.scss',
})
export class FieldMappingWiresComponent {
  private readonly anchors = inject(FieldMappingAnchorService);

  readonly rows = input.required<MappingRow[]>();
  readonly resourceColorVar = input.required<(resource: string) => string>();
  readonly isApproximated = input.required<(row: MappingRow) => boolean>();
  readonly tempWire = input<FmTempWire | null>(null);
  /** Not-yet-accepted candidates from the "Suggest mappings" button (see FieldMappingCanvasComponent) —
   *  rendered as a lighter dashed wire the user clicks to accept, distinct from an already-real mapped
   *  row's own dashed wire (which means "approximated", not "unconfirmed"). */
  readonly suggestions = input<MappingSuggestion[]>([]);

  readonly wireClick = output<{ resource: string; tableName: string; targetName: string }>();
  readonly suggestionClick = output<{ resource: string; tableName: string; targetName: string }>();

  onPathClick(rowKey: string): void {
    const [resource, tableName, targetPart] = rowKey.split('::');
    const targetName = targetPart.split('#')[0];
    this.wireClick.emit({ resource, tableName, targetName });
  }

  onSuggestionClick(rowKey: string): void {
    const [resource, tableName, targetName] = rowKey.split('::');
    this.suggestionClick.emit({ resource, tableName, targetName });
  }

  readonly paths = computed<FmWirePath[]>(() => {
    // Read version/pan/zoom so this recomputes whenever any anchor registers/moves OR the viewport
    // itself changes. Model-space coordinates are pan/zoom-invariant by construction (leftCenter/
    // rightCenter divide the scale back out), so none of these three values are actually used in the
    // math below — but getBoundingClientRect() is a plain, non-reactive DOM read, and without explicitly
    // depending on pan here, a pan-only interaction (drag, scrollbar, fit-to-view) would never re-trigger
    // this computed at all, leaving every wire's `d` frozen at stale rects from whenever version/zoom
    // last happened to change — visibly detached from the cards, which keep moving live via pure CSS.
    this.anchors.version();
    this.anchors.pan();
    this.anchors.zoom();
    const out: FmWirePath[] = [];
    for (const row of this.rows()) {
      const key = `${row.resource}::${row.tableName}::${row.targetName}`;
      const targetAnchor = this.anchors.anchorFor(key);
      if (!targetAnchor) continue;
      const b = this.anchors.leftCenter(targetAnchor);
      const stroke = this.resourceColorVar()(row.resource);
      const approximated = this.isApproximated()(row);

      if (row.mode === 'childJson' && row.childNodeId) {
        const a = this.sourceAnchorPoint(row.childNodeId);
        if (a) out.push({ rowKey: key, d: this.bezier(a, b), stroke, dashed: true });
        continue;
      }
      row.sources.forEach((s, i) => {
        const a = this.sourceAnchorPoint(s.fhirPath);
        if (a) out.push({ rowKey: `${key}#${i}`, d: this.bezier(a, b), stroke, dashed: approximated });
      });
    }
    return out;
  });

  /** Same anchor-lookup mechanism as `paths` above, just against the suggestions list instead of the
   *  real rows — both the source leaf and the destination column already register an anchor whether or
   *  not a real mapping exists yet (a column renders regardless of mapping state), so no new anchor
   *  plumbing is needed to draw a wire for something not yet confirmed. */
  readonly suggestionPaths = computed<FmSuggestionPath[]>(() => {
    this.anchors.version();
    this.anchors.pan();
    this.anchors.zoom();
    const out: FmSuggestionPath[] = [];
    for (const s of this.suggestions()) {
      const row = s.row;
      const key = `${row.resource}::${row.tableName}::${row.targetName}`;
      const targetAnchor = this.anchors.anchorFor(key);
      const source = row.sources[0];
      if (!targetAnchor || !source) continue;
      const a = this.sourceAnchorPoint(source.fhirPath);
      if (!a) continue;
      const b = this.anchors.leftCenter(targetAnchor);
      out.push({ rowKey: key, d: this.bezier(a, b), confidence: s.confidence });
    }
    return out;
  });

  readonly tempPath = computed<{ d: string; stroke: string } | null>(() => {
    this.anchors.pan();
    const temp = this.tempWire();
    if (!temp) return null;
    const a = this.sourceAnchorPoint(temp.fromId);
    if (!a) return null;
    const b = this.anchors.toViewportPoint(temp.toClientX, temp.toClientY);
    return { d: this.bezier(a, b), stroke: temp.stroke };
  });

  /** Walks up the dot-separated id until a registered anchor is found (handles collapsed ancestors). */
  private sourceAnchorPoint(id: string): { x: number; y: number } | null {
    let current: string | null = id;
    while (current) {
      const rect = this.anchors.anchorFor(current);
      if (rect) return this.anchors.rightCenter(rect);
      const lastDot = current.lastIndexOf('.');
      current = lastDot > 0 ? current.slice(0, lastDot) : null;
    }
    return null;
  }

  private bezier(a: { x: number; y: number }, b: { x: number; y: number }): string {
    const dx = Math.max(60, Math.abs(b.x - a.x) / 2);
    return `M ${a.x} ${a.y} C ${a.x + dx} ${a.y}, ${b.x - dx} ${b.y}, ${b.x} ${b.y}`;
  }
}
