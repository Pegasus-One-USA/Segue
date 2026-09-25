import { Component, HostBinding, computed, inject, input, output, signal, effect } from '@angular/core';
import { A11yModule } from '@angular/cdk/a11y';
import { FormsModule } from '@angular/forms';
import {
  MappingRow, MappingInstanceSelection, resolveArrayPolicy, isReferenceCandidate, defaultInstanceType,
  wholeNodeInstanceIndex, instanceCriteriaPredicate,
} from './field-mapping-model';
import { MappingValueType } from '../../../../mapping-profiles/models/mapping-profile.model';
import { DestinationTypeV2 as DestinationType } from '../../../../models/destination-configuration-v2.model';
import { ToastService } from '../../../../services/toast.service';
import {
  TransformationRulesService, TransformationRule, TransformNodeType, TransformNodeSchema, TransformArrayMode,
  TRANSFORM_NODE_DEFAULT_VALUE_TYPES,
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
  /** The workflow being edited. A rule is saved against it (Workflow scope) so it applies to THIS pipeline
   *  only — the broader tiers key on (resource type, destination column) and so apply to every workflow that
   *  maps the same column, which is how a rule authored in one pipeline started transforming another's.
   *  Null while the workflow is unsaved: nothing can be authored against an id that does not exist yet. */
  readonly workflowId = input<string | null>(null);
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
  /** For a whole-node ('childJson') row only: the display label of the repeating node it reads — its own,
   *  when that node is the array (Patient.name), or the nearest enclosing one when it is not
   *  (Patient.contact.name repeats through "Contact"). Null means the node does not repeat at all, which
   *  hides the instance picker. Resolved by the host, which is the only side holding the source tree
   *  (FieldMappingCanvasComponent.childArrayLabelFor) — a childJson row carries no `sources`, so unlike an
   *  ordinary field mapping there is no per-source `arrays` metadata on the row itself to read it from. */
  readonly childArrayLabel = input<string | null>(null);

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
  readonly ruleSaving = signal(false);
  readonly ruleDeleting = signal(false);

  readonly ruleSchema = computed<TransformNodeSchema | undefined>(() =>
    this.nodeSchemas().find(s => s.nodeType === this.ruleNodeType()));

  constructor() {
    effect(() => this.draft.set(this.withForcedAggregate(structuredClone(this.row()))));
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
        sourceField: this.ruleSourceField(row),
        resourcePipelineRouteId: this.workflowId() ?? undefined,
        // Only THIS workflow's rule may show here. Without this the popover opened in "update" mode over a
        // rule some other pipeline had authored against the same column.
        workflowScopedOnly: true,
        // A rule authored before the workflow's first save is stored unattached, so on an unsaved pipeline
        // this lookup found nothing and the popover reopened in "create" mode over a rule that already
        // exists — writing another row for the same column on every visit.
        includePending: true,
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
          } else {
            // Nothing authored yet — start on whichever node type actually fits this row's value shape.
            // A childJson row's value is a whole JSON array (ArrayPolicy.StoreJson), which is what
            // ArrayListOperationsNode alone knows how to unwrap into real items; every other node would see
            // one opaque blob. Still only a starting point — the Type dropdown offers all of them.
            this.ruleNodeType.set(row.mode === 'childJson' ? 'ArrayListOperations' : 'StringNormalization');
          }
        },
        error: () => this.existingRule.set(null),
      });
  }

  toggleRuleSection(): void {
    const opening = !this.ruleSectionOpen();
    this.ruleSectionOpen.set(opening);
    if (opening && !this.existingRule()) {
      this.ruleConfig.set(applyNodeDefaults(this.ruleSchema(), {}));
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
        // Workflow scope, not Field — see workflowId's own comment. Field scope is what made these rules
        // tenant-wide, and no UI ever authored the narrower Workflow tier the resolver already supported.
        scope: 'Workflow',
        resourcePipelineRouteId: this.workflowId(),
        nodeType: this.ruleNodeType(),
        config: this.ruleConfig(),
        destinationType,
        resourceType: row.resource,
        destinationField: row.targetName,
        sourceField: this.ruleSourceField(row),
        order: 0,
        onNull: 'Skip',
        errorPolicy: 'NullOut',
        isEnabled: true,
        // See resolveArrayMode.
        arrayMode: this.resolveArrayMode(),
        executionPhase: 'PostMapping',
        // A rule that reshapes the value (e.g. DateMathAge turning a Date into an Integer) must declare
        // that output type, or CreateMappingProfileRequestValidator falls back to comparing the RAW
        // source type against the column and rejects the save — "'BirthDateAge' is a int column (expects
        // Integer), but this field is mapped as Date." The Rules dialog has always sent this; saving the
        // same rule from this inline popover left it null, so which UI attached the rule decided whether
        // the workflow could be saved at all.
        //
        // NumberCast is a special case: its own "Target type" config field (integer/decimal — see
        // TransformNodeConfigSchemas.cs) is what actually decides the output type, not the node type alone
        // (unlike DateMathAge, which always produces an Integer). Blindly using the static per-node-type
        // default here always declared 'Decimal' (NumberCast's schema default), even when the author had
        // explicitly picked "integer" in this same popover — so an integer-typed destination column always
        // failed CreateMappingProfileRequestValidator's mismatch check regardless of what was configured.
        expectedValueType: this.resolveExpectedValueType(),
      })
      .subscribe({
        next: saved => {
          this.existingRule.set(saved);
          this.ruleSaving.set(false);
          this.toast.success('Transformation rule saved', `${saved.nodeType} will now run on ${row.targetName}.`);
        },
        error: err => {
          this.ruleSaving.set(false);
          this.toast.error('Could not save the transformation rule', err?.error?.detail ?? err?.message ?? '');
        },
      });
  }

  /** Node types that operate ON a collection (first/last/count/join/…) and so must see the whole array — the
   *  client-side mirror of ITransformNode.AcceptsCollections. Running one per element would count 1 per name. */
  private static readonly COLLECTION_NODE_TYPES: ReadonlySet<TransformNodeType> = new Set<TransformNodeType>(['ArrayListOperations']);

  /** 'PerItem' exactly when the value can be an array — a whole-node row not pinned to one index — AND the node
   *  converts a single value. The node then runs once per element and the results are reassembled for the
   *  column, instead of the whole array being handed to a scalar node as one opaque string (which parsed the
   *  JSON source text itself as a value). A collection node keeps 'Whole': fanning it out would apply every
   *  aggregate to a single element.
   *
   *  An existing rule keeps the mode it was saved with while its node type is unchanged, so editing an
   *  unrelated setting never silently changes what the column receives — there is no control for this mode. */
  private resolveArrayMode(): TransformArrayMode {
    const existing = this.existingRule();
    if (existing?.arrayMode && existing.nodeType === this.ruleNodeType()) return existing.arrayMode;
    return this.isChildJson() && this.valueCanBeArray()
      && !FieldMappingJoinPopoverComponent.COLLECTION_NODE_TYPES.has(this.ruleNodeType())
      ? 'PerItem'
      : 'Whole';
  }

  // Both NumberCast and DateTimeFormat have a "targetType" config field (see TransformNodeConfigSchemas.cs)
  // that is what the author actually used to pick the output type in this popover, and it overrides the node
  // type's static default when present. Every other node type has no such ambiguity (its output type is fixed
  // by the node type alone) and keeps using the static per-node-type default.
  private static readonly TARGET_TYPE_VALUE_TYPES: Partial<Record<string, MappingValueType>> = {
    integer: 'Integer',
    decimal: 'Decimal',
    date: 'Date',
    dateTime: 'DateTime',
    // MappingValueType has no separate 'Instant' tier — an instant-typed column is validated the same as
    // DateTime (see MappingImportService's DB-type mapping), so both targetType values fold to the same tier.
    instant: 'DateTime',
  };

  private resolveExpectedValueType(): MappingValueType | null {
    if (this.ruleNodeType() === 'NumberCast' || this.ruleNodeType() === 'DateTimeFormat') {
      const targetType = this.ruleConfig()['targetType'] as string | undefined;
      const resolved = targetType ? FieldMappingJoinPopoverComponent.TARGET_TYPE_VALUE_TYPES[targetType] : undefined;
      if (resolved) return resolved;
    }
    // DateMathAge's static default (Integer) only holds for its "age" operation — DateMathAgeNode's "add" and
    // "shift" operations both return a "yyyy-MM-dd" date STRING instead (a de-identification date-shift, not an
    // age calculation), so a rule configured for either must declare Date, or the runtime coercion this powers
    // (TransformNodeExecutors.CoerceToExpectedValueType) tries to long.TryParse a date string, fails, and the
    // unconverted string still hits Postgres's 42804 on a date-typed destination column.
    if (this.ruleNodeType() === 'DateMathAge') {
      const operation = this.ruleConfig()['operation'] as string | undefined;
      if (operation === 'add' || operation === 'shift') return 'Date';
    }
    return TRANSFORM_NODE_DEFAULT_VALUE_TYPES[this.ruleNodeType()] ?? null;
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
    // Never on a whole-node row: its "All records" already writes the entire node as one JSON array on one
    // row (ArrayPolicy.StoreJson), so there is no row duplication to prevent and no delimited string to
    // aggregate into — stamping 'csv' there would only persist a setting nothing reads.
    if (row.mode === 'childJson') return row;
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

  /** What a transformation rule is keyed by on the source side, for BOTH row shapes. An ordinary 'value'
   *  row has a real source field; a 'childJson' row has `sources: []` (see MappingRow.sources) and carries its
   *  path as childNodeId instead — passing null for it there would have keyed the rule to "any source field"
   *  on that column, so a rule authored on one array node would have been picked up by any other mapping onto
   *  the same column. Both shapes already agree on the format the backend re-derives at run time
   *  (RuleSourceFieldFormat.FromJsonPath): serializeRowsFlat sends the childJson row's path as childNodeId,
   *  which workflow-build-assembler-v2 turns into the same "$.name" JsonPath that normalizes back to
   *  "Patient.name" — exactly what is persisted here. */
  private ruleSourceField(row: MappingRow): string | null {
    return row.sources[0]?.fhirPath ?? row.childNodeId ?? null;
  }

  /** The whole-node-as-JSON row shape (ArrayPolicy.StoreJson) — no source chips, and a value that reaches
   *  a transformation rule as one JSON string rather than a scalar. */
  isChildJson = computed(() => this.draft()?.mode === 'childJson');
  /** A whole-node row has no per-source `arrays` metadata to read (its `sources` is empty by construction),
   *  so whether it repeats is the host's answer — see childArrayLabel. */
  hasArrayAncestors = computed(() =>
    this.isChildJson()
      ? this.childArrayLabel() !== null
      : (this.draft()?.sources[0]?.arrays?.length ?? 0) > 0);
  /** True while a whole-node row still reads every repeat — what it has always done, and what both of its
   *  hints describe. False once one instance is singled out, since the value is then a single JSON object
   *  rather than a JSON array. */
  readsWholeNode = computed(() => {
    const instance = this.draft()?.instance;
    // A criteria selection narrows the node too, even though it resolves to a filter rather than an index.
    return wholeNodeInstanceIndex(instance) === null && instanceCriteriaPredicate(instance) === null;
  });
  /** Whether the whole-node value can arrive as a JSON array — unlike readsWholeNode (which drives the hints),
   *  a criteria selection counts: its "[?field=value]" filter selects EVERY matching element, so two phone
   *  numbers still arrive as an array. Only a first/nth selection pins the value to one element. */
  valueCanBeArray = computed(() => wholeNodeInstanceIndex(this.draft()?.instance) === null);
  /** Names the single instance a whole-node row reads, for the hints — only ever read while readsWholeNode()
   *  is false, so the "every instance" case has no phrasing here. */
  instanceSummary = computed(() => {
    const d = this.draft();
    if (d?.instance?.type === 'nth') return `instance #${Math.max(1, Math.floor(d.instance.n ?? 1))}`;
    // Deliberately not restating the criteria itself — its three inputs sit directly above this line, and
    // repeating them back reads as noise rather than as confirmation.
    if (d?.instance?.type === 'criteria') return 'the matching instances';
    return 'the first instance';
  });
  /** The effective instance selection shown in the picker — an unset one means different things per row
   *  shape (see defaultInstanceType), so the fallback can't be a literal 'first' in the template. */
  instanceType = computed<MappingInstanceSelection['type']>(() => {
    const d = this.draft();
    return d ? (d.instance?.type ?? defaultInstanceType(d)) : 'first';
  });
  isJoin = computed(() => (this.draft()?.sources.length ?? 0) > 1);
  /** True while `row` is a deliberately narrowed single-source VIEW of a real join with more sources than
   *  are actually shown here (see FieldMappingCanvasComponent.popoverDisplayRow) — gates the "Show all N
   *  sources" link, the only sign in this popover that anything is currently hidden. False for an
   *  ordinary single-source mapping (totalSourceCount would equal draft().sources.length, 1, there too). */
  isNarrowedView = computed(() => (this.draft()?.sources.length ?? 0) < this.totalSourceCount());

  onShowAllSources(): void { this.showAllSources.emit(); }
  arrayAncestorLabel = computed(() => {
    if (this.isChildJson()) return this.childArrayLabel() ?? '';
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

  /** Parses the "Instance #" input into a valid 1-based instance number (1 = first). Falls back to 1
   *  (rather than a bare `+value || 1`, which would be fine here since 0 was never a meaningful typed
   *  value for a 1-based field — kept as its own named method anyway to match
   *  field-mapping-list.component.ts's identical helper and stay consistent if the minimum ever changes). */
  parseInstanceNumber(raw: string): number {
    const n = Number(raw);
    return Number.isFinite(n) && n >= 1 ? Math.trunc(n) : 1;
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
