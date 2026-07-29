import { Component, HostBinding, computed, input, output, signal, effect } from '@angular/core';
import { A11yModule } from '@angular/cdk/a11y';
import { MappingRow, MappingInstanceSelection, resolveArrayPolicy, isReferenceField } from './field-mapping-model';

/**
 * Join order/delimiter + array instance-selection editor. Opens either from a wire click or from the
 * keyboard-operable mapping-list row (both routes call the same open path in the canvas orchestrator).
 * Uses CdkTrapFocus — the one deliberate exception to "no CDK" in this feature, since drag-drop is what
 * was ruled out, not the unrelated a11y sub-package, and there's no focus-trap utility anywhere else in
 * this codebase to reuse. cdkTrapFocusAutoCapture both moves focus in on open and restores it to
 * whatever was focused before (the wire or the mapping-list's Edit button) when this is removed from
 * the DOM — no manual invoker-tracking needed.
 */
@Component({
  selector: 'app-field-mapping-join-popover',
  standalone: true,
  imports: [A11yModule],
  templateUrl: './field-mapping-join-popover.component.html',
  styleUrl: './field-mapping-join-popover.component.scss',
})
export class FieldMappingJoinPopoverComponent {
  readonly row = input.required<MappingRow>();
  /** Every resource selected for this destination — populates the "Resolves to" picker for a reference
   *  field, offering resources beyond whichever one this row itself belongs to. */
  readonly allResources = input<string[]>([]);

  readonly save = output<MappingRow>();
  readonly remove = output<void>();
  readonly closed = output<void>();

  readonly draft = signal<MappingRow | null>(null);

  constructor() {
    effect(() => this.draft.set(structuredClone(this.row())));
  }

  // ── drag-by-header (position: fixed, so plain viewport pixels — no canvas pan/zoom to correct for) ──
  private readonly position = signal<{ x: number; y: number }>({ x: 40, y: 96 });
  private dragOffset: { dx: number; dy: number } | null = null;

  @HostBinding('style.left.px') get hostLeft(): number { return this.position().x; }
  @HostBinding('style.top.px') get hostTop(): number { return this.position().y; }

  onHeadPointerDown(ev: PointerEvent): void {
    if (ev.button !== 0) return;
    // Don't start a drag from the close ("✕") button — setPointerCapture on the head would otherwise
    // redirect the subsequent pointerup (and the click derived from it) away from the button, silently
    // swallowing the click before onClose ever fires.
    if ((ev.target as HTMLElement).closest('button')) return;
    const el = ev.currentTarget as HTMLElement;
    el.setPointerCapture(ev.pointerId);
    const pos = this.position();
    this.dragOffset = { dx: ev.clientX - pos.x, dy: ev.clientY - pos.y };
  }

  onHeadPointerMove(ev: PointerEvent): void {
    if (!this.dragOffset) return;
    this.position.set({ x: ev.clientX - this.dragOffset.dx, y: ev.clientY - this.dragOffset.dy });
  }

  onHeadPointerUp(ev: PointerEvent): void {
    const el = ev.currentTarget as HTMLElement;
    if (el.hasPointerCapture(ev.pointerId)) el.releasePointerCapture(ev.pointerId);
    this.dragOffset = null;
  }

  hasArrayAncestors = computed(() => (this.draft()?.sources[0]?.arrays?.length ?? 0) > 0);
  isJoin = computed(() => (this.draft()?.sources.length ?? 0) > 1);
  arrayAncestorLabel = computed(() => {
    const arrays = this.draft()?.sources[0]?.arrays;
    return arrays?.length ? arrays[arrays.length - 1] : '';
  });

  isReferenceField = isReferenceField;
  otherResources = computed(() => {
    const resource = this.draft()?.resource;
    return this.allResources().filter(r => r !== resource);
  });

  approximationNote = computed(() => {
    const d = this.draft();
    if (!d) return null;
    const { approximated } = resolveArrayPolicy(d);
    return approximated
      ? 'Preview only — this configuration has no exact equivalent in the pipeline engine yet; the build will send a best-effort approximation.'
      : null;
  });

  moveSource(i: number, dir: -1 | 1): void {
    this.draft.update(d => {
      if (!d) return d;
      const sources = [...d.sources];
      const j = i + dir;
      if (j < 0 || j >= sources.length) return d;
      [sources[i], sources[j]] = [sources[j], sources[i]];
      return { ...d, sources };
    });
  }

  removeSource(i: number): void {
    this.draft.update(d => {
      if (!d) return d;
      const sources = d.sources.filter((_, idx) => idx !== i);
      return { ...d, sources };
    });
  }

  onDelimiterInput(value: string): void {
    this.draft.update(d => (d ? { ...d, delimiter: value } : d));
  }

  onInstanceTypeChange(type: MappingInstanceSelection['type']): void {
    this.draft.update(d => (d ? { ...d, instance: { ...(d.instance ?? { type: 'first' }), type } } : d));
  }

  onInstanceField(patch: Partial<MappingInstanceSelection>): void {
    this.draft.update(d => (d ? { ...d, instance: { ...(d.instance ?? { type: 'first' }), ...patch } } : d));
  }

  onReferenceResourceChange(value: string): void {
    this.draft.update(d => (d ? { ...d, referencesResource: value || undefined } : d));
  }

  onSave(): void {
    const d = this.draft();
    if (d) this.save.emit(d);
  }

  onRemove(): void { this.remove.emit(); }

  onClose(): void { this.closed.emit(); }

  onKeydown(ev: KeyboardEvent): void {
    if (ev.key === 'Escape') { ev.preventDefault(); this.closed.emit(); }
  }
}
