import { Injectable, signal, computed } from '@angular/core';

const MIN_ZOOM = 0.4;
const MAX_ZOOM = 1.6;

/**
 * Component-scoped (provided by field-mapping-canvas.component, NOT providedIn:'root') rect registry
 * shared by tree nodes, target-card column rows, and the wire overlay — one shared source of truth for
 * "where is id X on screen right now" instead of every consumer re-querying the DOM independently.
 * Also owns this canvas's pan/zoom viewport state (mirroring the main workflow canvas's
 * CanvasService, but scoped per mapping-canvas instance rather than a root singleton, and with an
 * added fitToView since that reuses this same anchor registry).
 *
 * The wire SVG and every card are siblings inside one origin content element (registered via
 * setOrigin) that carries the pan/zoom CSS transform — a child's position relative to that shared
 * origin is stable at any pan offset without separately compensating for it (exactly like it was
 * already stable at any scroll offset before zoom/pan existed), but a `scale()` transform means the
 * raw rect delta comes out pre-multiplied by the current zoom, so rightCenter/leftCenter/
 * toViewportPoint divide it back out to keep returning true model-space coordinates.
 */
@Injectable()
export class FieldMappingAnchorService {
  private originEl: HTMLElement | null = null;
  private readonly elements = new Map<string, HTMLElement>();

  /** Bumped on every recompute so consumers (the wire overlay) know to re-read anchors. */
  readonly version = signal(0);

  private readonly _pan = signal<{ x: number; y: number }>({ x: 0, y: 0 });
  private readonly _zoom = signal<number>(1);
  private readonly _viewportSize = signal<{ width: number; height: number }>({ width: 0, height: 0 });

  readonly pan = this._pan.asReadonly();
  readonly zoom = this._zoom.asReadonly();
  readonly viewportSize = this._viewportSize.asReadonly();

  readonly zoomPercent = computed(() => Math.round(this._zoom() * 100) + '%');

  // ── vertical scrollbar (replaces vertical drag-pan/wheel with a real, bounded scroll range —
  // horizontal pan stays free-form via drag, unaffected by any of this) ──
  // Floor for the scrollable range — matches .fm-canvas-inner's fixed CSS height, used until anything
  // registers. Cards (e.g. the source payload tree) are height:auto and can render far taller than this
  // for a large payload, so the real range below tracks the actual registered content instead of
  // staying capped here — otherwise a tall card's bottom rows would be visually present but stuck
  // beyond how far panning/scrolling is allowed to go.
  private static readonly VIRTUAL_HEIGHT = 2600;
  private static readonly CONTENT_BOTTOM_PADDING = 200;

  setViewportSize(width: number, height: number): void {
    this._viewportSize.set({ width, height });
  }

  /** The tallest extent of any currently-registered anchor (tree leaves, group headers, column rows),
   *  in model-space, with the same VIRTUAL_HEIGHT floor as before — falls back to that floor before
   *  anything has rendered yet, or once whatever's registered still fits inside it. */
  private readonly modelContentHeight = computed(() => {
    this.version(); // re-run whenever anchors are registered/unregistered/refreshed
    const bounds = this.boundingBoxModel();
    const measured = bounds ? bounds.maxY + FieldMappingAnchorService.CONTENT_BOTTOM_PADDING : 0;
    return Math.max(FieldMappingAnchorService.VIRTUAL_HEIGHT, measured);
  });

  /** How far content can travel before its bottom edge reaches the viewport's bottom edge — the
   *  range a scrollbar thumb travels across. 0 once everything already fits (no scrolling needed). */
  readonly maxScrollY = computed(() =>
    Math.max(0, this.modelContentHeight() * this._zoom() - this._viewportSize().height));

  /** Current scroll position derived from pan.y — 0 at the top of content, maxScrollY at the bottom. */
  readonly scrollY = computed(() => Math.min(this.maxScrollY(), Math.max(0, -this._pan().y)));

  /** Thumb height as a fraction of the track; 1 means content already fits (no scrollbar needed). */
  readonly scrollThumbFraction = computed(() => {
    const contentHeight = this.modelContentHeight() * this._zoom();
    return contentHeight > 0 ? Math.min(1, this._viewportSize().height / contentHeight) : 1;
  });

  /**
   * Clamps a candidate Y pan to the scrollbar-compatible range [-maxScrollY, 0] — used by drag-pan and
   * wheel-scroll. fitToView/resetView/setZoom deliberately do NOT go through this: they have their own
   * positioning logic (e.g. centering content shorter than the viewport can legitimately need pan.y > 0,
   * which a scrollbar has no equivalent of).
   */
  clampPanY(y: number): number {
    const max = this.maxScrollY();
    return Math.max(-max, Math.min(0, y));
  }

  setScrollY(scrollY: number): void {
    const max = this.maxScrollY();
    const clamped = Math.max(0, Math.min(max, scrollY));
    this._pan.update(p => ({ ...p, y: -clamped }));
  }

  readonly transformStyle = computed(() => {
    const { x, y } = this._pan();
    return `translate(${x}px, ${y}px) scale(${this._zoom()})`;
  });

  setOrigin(el: HTMLElement): void {
    this.originEl = el;
  }

  register(id: string, el: HTMLElement): void {
    this.elements.set(id, el);
    this.version.update(v => v + 1);
  }

  unregister(id: string): void {
    this.elements.delete(id);
    this.version.update(v => v + 1);
  }

  /** Re-measures every registered element — call on resize of the origin container. */
  refreshAll(): void {
    this.version.update(v => v + 1);
  }

  anchorFor(id: string): DOMRect | null {
    const el = this.elements.get(id);
    if (!el) return null;
    return this.clampToScrollableAncestor(el, el.getBoundingClientRect());
  }

  /**
   * When `el` sits inside a scrollable row region (.fm-source-rows/.fm-target-rows — scrollable
   * vertically via the "fit to screen" toggle, see FieldMappingSourceTreeComponent/
   * FieldMappingTargetCardComponent's fitMode) and is currently scrolled out of that region's visible
   * band, clamps the returned rect's vertical center to the region's own visible top/bottom edge instead
   * of the field's true, off-screen position. Without this, a wire to/from a row scrolled out of view is
   * drawn straight to that off-screen point, visibly breaking out past the card's edge into open canvas
   * space rather than stopping at the card boundary. Only clamps once the row's true center itself exits
   * the visible band (not merely if any part of it is clipped), so a still-mostly-visible row keeps its
   * natural wire position.
   */
  private clampToScrollableAncestor(el: HTMLElement, rect: DOMRect): DOMRect {
    const container = el.closest<HTMLElement>('.fm-source-rows, .fm-target-rows');
    if (!container) return rect;

    const containerRect = container.getBoundingClientRect();
    const centerY = rect.top + rect.height / 2;
    const clampedCenterY = Math.min(containerRect.bottom, Math.max(containerRect.top, centerY));
    if (clampedCenterY === centerY) return rect;

    return new DOMRect(rect.x, clampedCenterY, rect.width, 0);
  }

  /** Center-right point of a rect (source port position), in model-space coordinates. */
  rightCenter(rect: DOMRect): { x: number; y: number } {
    const origin = this.originRect();
    const z = this._zoom();
    return { x: (rect.right - origin.x) / z, y: (rect.top + rect.height / 2 - origin.y) / z };
  }

  /** Center-left point of a rect (target port position), in model-space coordinates. */
  leftCenter(rect: DOMRect): { x: number; y: number } {
    const origin = this.originRect();
    const z = this._zoom();
    return { x: (rect.left - origin.x) / z, y: (rect.top + rect.height / 2 - origin.y) / z };
  }

  /** A raw client point (e.g. current pointer position) converted into model-space coordinates. */
  toViewportPoint(clientX: number, clientY: number): { x: number; y: number } {
    const origin = this.originRect();
    const z = this._zoom();
    return { x: (clientX - origin.x) / z, y: (clientY - origin.y) / z };
  }

  private originRect(): { x: number; y: number } {
    if (!this.originEl) return { x: 0, y: 0 };
    const r = this.originEl.getBoundingClientRect();
    return { x: r.left, y: r.top };
  }

  // ── pan / zoom ─────────────────────────────────────────────────────────────

  setPan(x: number, y: number): void {
    this._pan.set({ x, y });
  }

  movePan(dx: number, dy: number): void {
    const { x, y } = this._pan();
    this._pan.set({ x: x + dx, y: y + dy });
  }

  /**
   * Changes zoom, optionally keeping the model-space point currently under (anchorClientX,
   * anchorClientY) visually stationary — the classic "zoom toward the cursor" behavior. Without an
   * anchor, zoom changes around the model origin (used for the zoom-dock buttons, matching the main
   * workflow canvas's CanvasService, which doesn't anchor button-triggered zoom either).
   */
  setZoom(newZoom: number, anchorClientX?: number, anchorClientY?: number): void {
    const clamped = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, newZoom));
    if (anchorClientX === undefined || anchorClientY === undefined || !this.originEl) {
      this._zoom.set(clamped);
      return;
    }

    const origin = this.originEl.getBoundingClientRect();
    const curZoom = this._zoom();
    const curPan = this._pan();
    const scaleRatio = 1 - clamped / curZoom;
    const dx = anchorClientX - origin.left;
    const dy = anchorClientY - origin.top;

    this._pan.set({ x: curPan.x + dx * scaleRatio, y: curPan.y + dy * scaleRatio });
    this._zoom.set(clamped);
  }

  resetView(): void {
    this._pan.set({ x: 0, y: 0 });
    this._zoom.set(1);
  }

  /**
   * Frames every currently-registered anchor (tree leaves + column rows) inside the given viewport
   * size, with some breathing room. Falls back to resetView() if nothing is registered yet (e.g. the
   * source tree hasn't rendered its first frame).
   */
  fitToView(viewportWidth: number, viewportHeight: number, padding = 60): void {
    const bounds = this.boundingBoxModel();
    if (!bounds) {
      this.resetView();
      return;
    }

    const contentWidth = Math.max(1, bounds.maxX - bounds.minX);
    const contentHeight = Math.max(1, bounds.maxY - bounds.minY);
    const availableWidth = Math.max(1, viewportWidth - padding * 2);
    const availableHeight = Math.max(1, viewportHeight - padding * 2);
    const fitZoom = Math.min(
      MAX_ZOOM,
      Math.max(MIN_ZOOM, Math.min(availableWidth / contentWidth, availableHeight / contentHeight)),
    );
    const centerX = (bounds.minX + bounds.maxX) / 2;
    const centerY = (bounds.minY + bounds.maxY) / 2;

    this._zoom.set(fitZoom);
    this._pan.set({ x: viewportWidth / 2 - centerX * fitZoom, y: viewportHeight / 2 - centerY * fitZoom });
  }

  private boundingBoxModel(): { minX: number; minY: number; maxX: number; maxY: number } | null {
    if (!this.originEl || this.elements.size === 0) return null;

    const origin = this.originEl.getBoundingClientRect();
    const z = this._zoom();
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;

    for (const el of this.elements.values()) {
      const r = el.getBoundingClientRect();
      minX = Math.min(minX, (r.left - origin.x) / z);
      minY = Math.min(minY, (r.top - origin.y) / z);
      maxX = Math.max(maxX, (r.right - origin.x) / z);
      maxY = Math.max(maxY, (r.bottom - origin.y) / z);
    }

    return minX === Infinity ? null : { minX, minY, maxX, maxY };
  }
}
