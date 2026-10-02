import { Component, HostBinding, computed, inject, input, output, signal, effect, untracked } from '@angular/core';
import { A11yModule } from '@angular/cdk/a11y';
import { FormsModule } from '@angular/forms';
import { MappingRow, MappingInstanceSelection, resolveArrayPolicy, isReferenceCandidate } from './field-mapping-model';
import { DestinationType } from '../../../../destination-connections/models/destination-configuration.model';
import { ToastService } from '../../../../services/toast.service';
import { UnsavedChangesRegistryService } from '../../../../core/services/unsaved-changes-registry.service';
import {
  TransformationRulesService, TransformationRule, TransformNodeType, TransformNodeSchema,
} from './transformation-rules.service';
import { RuleConfigFormComponent, applyNodeDefaults } from './rule-config-form/rule-config-form.component';

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
  imports: [A11yModule, FormsModule, RuleConfigFormComponent],
  templateUrl: './field-mapping-join-popover.component.html',
  styleUrl: './field-mapping-join-popover.component.scss',
})
export class FieldMappingJoinPopoverComponent {
  readonly row = input.required<MappingRow>();
  /** Every resource selected for this destination — populates the "Resolves to" picker for a reference
   *  field, offering resources beyond whichever one this row itself belongs to. */
  readonly allResources = input<string[]>([]);
  /** See FieldMappingCanvasComponent's own doc comment — null hides the transformation-rule section
   *  entirely (this popover has no destination type to scope a rule against). */
  readonly rulesDestinationType = input<DestinationType | null>(null);
  /** The DestinationConfiguration these columns belong to — see the v2 popover for why the type alone
   *  cannot identify a destination. Null when unknown, which matches permissively. */
  readonly rulesDestinationConfigurationId = input<string | null>(null);
  /** Same resolver FieldMappingCanvasComponent already threads through its own cards (display-only,
   *  same role as its own dataTypeForTable) — used to show the target column's real type next to its
   *  name, rather than inventing one. Undefined (no live schema, e.g. a CSV/Mongo destination) just
   *  omits the badge. */
  readonly dataTypeForTable = input<(tableFullName: string, column: string) => string | undefined>(() => undefined);
  /** How many sources the REAL underlying row actually has — independent of `row` above, which may be a
   *  narrowed, single-source VIEW of it (see FieldMappingCanvasComponent.popoverDisplayRow). Defaults to
   *  0 so a host that never passes it (there are none today) just never shows "Show all sources" rather
   *  than throwing on a missing required input. */
  readonly totalSourceCount = input<number>(0);

  readonly save = output<MappingRow>();
  readonly remove = output<void>();
  readonly closed = output<void>();
  /** "Show all sources" was clicked (see isNarrowedView) — this popover never expands the view itself;
   *  it has no access to the other, currently-hidden sources at all (only `row`, already narrowed to
   *  one), so the host owns swapping back to the real, complete row. */
  readonly showAllSources = output<void>();

  readonly draft = signal<MappingRow | null>(null);

  private readonly rulesService = inject(TransformationRulesService);
  private readonly toast = inject(ToastService);

  // ── inline transformation rule (the connector IS the wiring; this is what happens to the value after) ──
  readonly nodeSchemas = signal<TransformNodeSchema[]>([]);
  /** The single rule currently in effect for this exact connector (Field-scope, keyed by resourceType +
   *  destinationField + sourceField) — null once loaded means "none yet", undefined means "still loading". */
  readonly existingRule = signal<TransformationRule | null | undefined>(undefined);
  readonly ruleSectionOpen = signal(false);
  readonly ruleNodeType = signal<TransformNodeType>('StringNormalization');
  readonly ruleConfig = signal<Record<string, string>>({});

  /** The rule as it stands on the SERVER — captured when one is loaded, when the section is opened
   *  for a row that has none, and after a successful save. Everything typed in the section lives in
   *  ruleConfig alone until "Add rule"/"Update rule" is pressed, so this is the only way to tell an
   *  edit that would be persisted from one that would be thrown away. Null until the first capture,
   *  which means "nothing to compare against" rather than "everything is unsaved". */
  private readonly ruleBaseline = signal<string | null>(null);

  private ruleSnapshot(): string {
    return JSON.stringify({ nodeType: this.ruleNodeType(), config: this.ruleConfig() });
  }

  private captureRuleBaseline(): void {
    // untracked because this runs inside the subscribe of loadRuleFor, which is itself called from an
    // effect(). Reading ruleNodeType/ruleConfig in that context registers them as dependencies OF THE
    // EFFECT, so every edit to the config re-ran the rule lookup — which then reset the node type and
    // silently undid what the author had just chosen. It only bites when the lookup resolves
    // synchronously (a test double, or any future cache), which is exactly why it is worth removing
    // rather than relying on the HTTP call always being slow enough to hide it.
    this.ruleBaseline.set(untracked(() => this.ruleSnapshot()));
  }

  /** The mapping draft as it was when this popover opened (or its row changed) — anything different is an
   *  unsaved edit: join order/delimiter, instance selection, JSON write mode, reference resource. */
  private readonly draftBaseline = signal<string | null>(null);

  /** Any unsaved edit at all — the mapping fields or the transformation rule. Registered with the app-wide
   *  unsaved-changes check, so leaving the PAGE (menu, back, sign out, refresh) asks first. Clicks inside the
   *  mapping screen (✕, another column) deliberately don't use it. */
  readonly hasUnsavedEdits = computed(() => {
    const baseline = this.draftBaseline();
    return this.hasUnsavedRule() || (baseline !== null && JSON.stringify(this.draft()) !== baseline);
  });

  /** Whether the rule section holds changes that have never reached the server. Closing the popover
   *  discards those with nothing left to recover — unlike a SAVED rule, which survives on the server
   *  even when the workflow itself is never saved. */
  readonly hasUnsavedRule = computed(() => {
    const baseline = this.ruleBaseline();
    return baseline !== null && this.ruleSnapshot() !== baseline;
  });

  /** Which exit is waiting on the "not saved" confirm, or null when none is. Both routes leave the
   *  popover and so discard the rule config: the ✕/Escape/Cancel route closes, and Save emits the
   *  mapping save — whose handler calls closePopover() itself, which is why guarding onClose() alone
   *  left the likeliest button of the two unguarded. */
  readonly pendingUnsavedRuleAction = signal<'close' | 'save' | null>(null);
  readonly ruleSaving = signal(false);
  readonly ruleDeleting = signal(false);

  readonly ruleSchema = computed<TransformNodeSchema | undefined>(() =>
    this.nodeSchemas().find(s => s.nodeType === this.ruleNodeType()));

  constructor() {
    effect(() => {
      const draft = this.withForcedAggregate(structuredClone(this.row()));
      this.draft.set(draft);
      // From the value itself, not by reading draft() — that would make this effect depend on every edit.
      this.draftBaseline.set(JSON.stringify(draft));
    });
    inject(UnsavedChangesRegistryService).register(() => this.hasUnsavedEdits());
    effect(() => this.loadRuleFor(this.row()));

    this.rulesService.getNodeSchemas().subscribe(schemas => this.nodeSchemas.set(schemas));
  }

  private loadRuleFor(row: MappingRow): void {
    const destinationType = this.rulesDestinationType();
    if (!destinationType) return;

    this.existingRule.set(undefined);
    this.ruleSectionOpen.set(false);
    this.rulesService
      .getEffectiveRules({
        destinationType,
        resourceType: row.resource,
        destinationField: row.targetName,
        sourceField: row.sources[0]?.fhirPath ?? null,
        destinationConfigurationId: this.rulesDestinationConfigurationId(),
      })
      .subscribe({
        next: rules => {
          // Multiple steps can legitimately chain at the Transformation Rules screen, but this popover only
          // ever authors one — the same single-rule-per-connector shape every rule in this session's
          // walkthrough was built to (chaining two here is exactly the duplicate-rule bug that nulled out
          // MRN/AgeYears earlier). If more than one is already in effect, show the first and leave the rest
          // untouched rather than silently collapsing them.
          const rule = rules[0] ?? null;
          this.existingRule.set(rule);
          if (rule) {
            this.ruleNodeType.set(rule.nodeType);
            this.ruleConfig.set({ ...rule.config });
            this.ruleSectionOpen.set(true);
          }
          this.captureRuleBaseline();
        },
        error: () => this.existingRule.set(null),
      });
  }

  toggleRuleSection(): void {
    const opening = !this.ruleSectionOpen();
    this.ruleSectionOpen.set(opening);
    if (opening && !this.existingRule()) {
      this.ruleConfig.set(applyNodeDefaults(this.ruleSchema(), {}));
      // Defaults are not an edit — only what the author changes after this point counts.
      this.captureRuleBaseline();
    }
  }

  onRuleNodeTypeChange(nodeType: TransformNodeType): void {
    this.ruleNodeType.set(nodeType);
    this.ruleConfig.set(applyNodeDefaults(this.nodeSchemas().find(s => s.nodeType === nodeType), {}));
  }

  saveRule(): void {
    const row = this.row();
    const destinationType = this.rulesDestinationType();
    if (!destinationType) return;

    this.ruleSaving.set(true);
    this.rulesService
      .save({
        id: this.existingRule()?.id ?? null,
        scope: 'Field',
        nodeType: this.ruleNodeType(),
        config: this.ruleConfig(),
        destinationType,
        // Which destination this rule belongs to. Omitting it stores a null, which matches ANY
        // destination of the same type — so a replacement destination inherits the rule and
        // delete-by-destination cannot identify it. Covered by a test on the POSTed body.
        destinationConfigurationId: this.rulesDestinationConfigurationId(),
        resourceType: row.resource,
        destinationField: row.targetName,
        sourceField: row.sources[0]?.fhirPath ?? null,
        order: 0,
        onNull: 'Skip',
        errorPolicy: 'NullOut',
        isEnabled: true,
        arrayMode: 'Whole',
        executionPhase: 'PostMapping',
      })
      .subscribe({
        next: saved => {
          this.existingRule.set(saved);
          this.ruleSaving.set(false);
          // The screen now matches the server, so nothing is pending discard.
          this.captureRuleBaseline();
          this.toast.success('Transformation rule saved', `${saved.nodeType} will now run on ${row.targetName}.`);
        },
        error: err => {
          this.ruleSaving.set(false);
          this.toast.error('Could not save the transformation rule', err?.error?.detail ?? err?.message ?? '');
        },
      });
  }

  deleteRule(): void {
    const rule = this.existingRule();
    if (!rule) return;

    this.ruleDeleting.set(true);
    this.rulesService.delete(rule.id).subscribe({
      next: () => {
        this.ruleDeleting.set(false);
        this.existingRule.set(null);
        this.ruleConfig.set(applyNodeDefaults(this.ruleSchema(), {}));
        // The rule is gone and the config is back to defaults, so there is nothing pending — without
        // this the baseline still describes the DELETED rule and closing or saving warns about
        // unsaved changes that do not exist.
        this.captureRuleBaseline();
        this.toast.success('Transformation rule removed', '');
      },
      error: err => {
        this.ruleDeleting.set(false);
        this.toast.error('Could not remove the transformation rule', err?.error?.detail ?? err?.message ?? '');
      },
    });
  }

  /** "All records" without combining (RepeatParent) duplicates the entire destination row once per array
   *  item — correct only for a genuine child/array destination table, where this choice doesn't even matter
   *  (serializeRowsFlat overrides the array policy to SeparateDestination there regardless). On any other
   *  (same-table) target it duplicates the whole row per array item, which is essentially always wrong — so
   *  "All records" always combines into one delimited string, with no way to opt out via the UI. Applied both
   *  when the user picks "All records" (onInstanceTypeChange) and when a row already saved with that
   *  combination is reopened (constructor effect above), so the checkbox — always disabled while type is
   *  'all' — never shows unchecked-but-locked. */
  private withForcedAggregate(row: MappingRow): MappingRow {
    if (row.instance?.type !== 'all' || row.instance.aggregate === 'csv') return row;
    return { ...row, instance: { ...row.instance, aggregate: 'csv' } };
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
  /** True while `row` is a deliberately narrowed single-source VIEW of a real join with more sources than
   *  are actually shown here (see FieldMappingCanvasComponent.popoverDisplayRow) — gates the "Show all N
   *  sources" link, the only sign in this popover that anything is currently hidden. False for an
   *  ordinary single-source mapping (totalSourceCount would equal draft().sources.length, 1, there too). */
  isNarrowedView = computed(() => (this.draft()?.sources.length ?? 0) < this.totalSourceCount());

  onShowAllSources(): void { this.showAllSources.emit(); }
  arrayAncestorLabel = computed(() => {
    const arrays = this.draft()?.sources[0]?.arrays;
    return arrays?.length ? arrays[arrays.length - 1] : '';
  });

  isReferenceCandidate = isReferenceCandidate;
  otherResources = computed(() => {
    const resource = this.draft()?.resource;
    return this.allResources().filter(r => r !== resource);
  });

  /** Compact "source → target" line under the header title — display-only, derived entirely from
   *  fields already shown elsewhere in this same popover (the source chip list, the target name). */
  headerSummarySource = computed(() => {
    const d = this.draft();
    if (!d) return '';
    if (d.mode === 'childJson') return d.childNodeId ?? '';
    return d.sources.length > 1 ? `${d.sources.length} fields` : (d.sources[0]?.fhirPath ?? '');
  });

  targetDataType = computed(() => {
    const d = this.draft();
    if (!d) return undefined;
    return this.dataTypeForTable()(d.tableName, d.targetName);
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
    this.draft.update(d => (d ? this.withForcedAggregate({ ...d, instance: { ...(d.instance ?? { type: 'first' }), type } }) : d));
  }

  onInstanceField(patch: Partial<MappingInstanceSelection>): void {
    this.draft.update(d => (d ? { ...d, instance: { ...(d.instance ?? { type: 'first' }), ...patch } } : d));
  }

  onReferenceResourceChange(value: string): void {
    this.draft.update(d => (d ? { ...d, referencesResource: value || undefined } : d));
  }

  onSave(): void {
    // Saving the MAPPING does not save the rule — the canvas handler closes the popover straight
    // after, taking any unsaved rule config with it.
    if (this.hasUnsavedRule()) {
      this.pendingUnsavedRuleAction.set('save');
      return;
    }
    this.emitSave();
  }

  private emitSave(): void {
    const d = this.draft();
    if (d) this.save.emit(d);
  }

  onRemove(): void { this.remove.emit(); }

  onClose(): void {
    // A configured-but-unsaved rule exists ONLY in this component. Closing used to drop it with no
    // warning and nothing on the server to recover — "I set concat and a | separator, forgot to press
    // Add rule, and it was gone".
    if (this.hasUnsavedRule()) {
      this.pendingUnsavedRuleAction.set('close');
      return;
    }
    this.closed.emit();
  }

  /** "Discard" on the confirm — the author has been told, so carry out whichever exit they asked for. */
  discardAndContinue(): void {
    const action = this.pendingUnsavedRuleAction();
    this.pendingUnsavedRuleAction.set(null);
    if (action === 'save') {
      this.emitSave();
      return;
    }
    this.closed.emit();
  }

  keepEditing(): void {
    this.pendingUnsavedRuleAction.set(null);
  }

  onKeydown(ev: KeyboardEvent): void {
    if (ev.key === 'Escape') { ev.preventDefault(); this.onClose(); }
  }
}
