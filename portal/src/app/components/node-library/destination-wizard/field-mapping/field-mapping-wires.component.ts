import { Component, computed, inject, input, output } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { MappingRow } from './field-mapping-model';
import { MappingSuggestion } from './field-mapping-automap.util';
import { FieldMappingAnchorService } from './field-mapping-anchor.service';

export interface FmWirePath {
  /** Unique per rendered wire (a join draws one wire per source, all sharing the same resource/table/
   *  target) — used only as the @for trackBy key; click/routing logic below reads the explicit fields
   *  next to it instead of re-parsing this string. */
  rowKey: string;
  resource: string;
  tableName: string;
  targetName: string;
  /** The EXACT source this one wire represents — the real MappingSourceRef.fhirPath from the row's own
   *  `sources[]` this wire was drawn for (see `paths` below), never inferred from index/order/visual
   *  position. Null only for a childJson (whole-node-as-JSON) wire, which has no individual source to
   *  single out — clicking it opens the whole row, there being nothing to narrow to. This is what lets a
   *  connector-line click resolve to its own specific source → target relationship instead of always
   *  opening every source mapped onto that target. */
  sourceFhirPath: string | null;
  d: string;
  stroke: string;
  dashed: boolean;
  /** Source leaf's display label (e.g. "Given Name") — shown in the hover tooltip only. */
  sourceLabel: string;
  /** Target column name — same tooltip-only role as sourceLabel. */
  targetLabel: string;
  /** Midpoint of the drawn curve (model-space), for the hover dot + tooltip anchor. Equal to the plain
   *  average of the two endpoints — the cubic bezier's control points are anchored at each endpoint's own
   *  y (see wireGeometry), so the curve's true point at t=0.5 always reduces to that average algebraically,
   *  no separate curve-sampling math needed. */
  midX: number;
  midY: number;
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

  /** `sourceFhirPath` is the exact source this wire was drawn for (see FmWirePath) — carried straight
   *  through from `p` untouched, never re-derived. Null for a childJson wire (open the whole row). */
  readonly wireClick = output<{ resource: string; tableName: string; targetName: string; sourceFhirPath: string | null }>();
  readonly suggestionClick = output<{ resource: string; tableName: string; targetName: string }>();

  onPathClick(p: FmWirePath): void {
    this.wireClick.emit({
      resource: p.resource, tableName: p.tableName, targetName: p.targetName, sourceFhirPath: p.sourceFhirPath,
    });
  }

  onSuggestionClick(rowKey: string): void {
    const [resource, tableName, targetName] = rowKey.split('::');
    this.suggestionClick.emit({ resource, tableName, targetName });
  }

  /** Space/Enter on a focused (keyboard) wire hit-path opens the same mapping config a click would —
   *  the hit path carries tabindex/role="button" in the template. preventDefault on Space stops the
   *  page from scrolling, the browser's default action for Space on a focusable non-form element. */
  onWireKeydown(event: KeyboardEvent, p: FmWirePath): void {
    if (event.key === 'Enter' || event.key === ' ' || event.key === 'Spacebar') {
      event.preventDefault();
      this.onPathClick(p);
    }
  }

  /** Same as onWireKeydown, for a suggestion wire's hit-path (accepts the suggestion instead). */
  onSuggestionKeydown(event: KeyboardEvent, rowKey: string): void {
    if (event.key === 'Enter' || event.key === ' ' || event.key === 'Spacebar') {
      event.preventDefault();
      this.onSuggestionClick(rowKey);
    }
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
        if (a) {
          const { d, mid } = this.wireGeometry(a, b);
          const lastDot = row.childNodeId.lastIndexOf('.');
          const sourceLabel = lastDot >= 0 ? row.childNodeId.slice(lastDot + 1) : row.childNodeId;
          out.push({
            rowKey: key, resource: row.resource, tableName: row.tableName, targetName: row.targetName,
            sourceFhirPath: null, d, stroke, dashed: true,
            sourceLabel, targetLabel: row.targetName, midX: mid.x, midY: mid.y,
          });
        }
        continue;
      }
      // rowKey still gets a `#i` suffix purely so @for's trackBy stays unique per source on this same
      // target — nothing downstream parses it any more (see onPathClick, which reads the explicit
      // resource/tableName/targetName/sourceFhirPath fields below instead); the real per-wire identity is
      // s.fhirPath itself, taken directly from this row's own `sources[]`, never index/order-derived.
      row.sources.forEach((s, i) => {
        const a = this.sourceAnchorPoint(s.fhirPath);
        if (a) {
          const { d, mid } = this.wireGeometry(a, b);
          out.push({
            rowKey: `${key}#${i}`, resource: row.resource, tableName: row.tableName, targetName: row.targetName,
            sourceFhirPath: s.fhirPath, d, stroke, dashed: approximated,
            sourceLabel: s.label, targetLabel: row.targetName, midX: mid.x, midY: mid.y,
          });
        }
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

  /** Same curve as bezier() above, plus the point at the curve's own t=0.5 for the hover dot/tooltip
   *  anchor. Both control points sit at their own endpoint's y (a.y / b.y respectively), so the standard
   *  cubic-bezier weights at t=0.5 (⅛, ⅜, ⅜, ⅛) collapse algebraically to the plain average of the two
   *  endpoints for both x and y — no need to actually sample the curve. */
  private wireGeometry(
    a: { x: number; y: number },
    b: { x: number; y: number },
  ): { d: string; mid: { x: number; y: number } } {
    return { d: this.bezier(a, b), mid: { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 } };
  }
}
