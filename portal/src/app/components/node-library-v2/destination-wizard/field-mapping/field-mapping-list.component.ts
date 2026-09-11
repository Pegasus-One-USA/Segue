import { Component, HostBinding, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { MappingRow, MappingInstanceSelection, isReferenceCandidate } from './field-mapping-model';
import { FmTreeNode, flattenLeaves } from './field-mapping-tree.util';
import { nearestArrayGroupId } from './field-mapping-summary.model';
import { FieldMappingAnchorService } from './field-mapping-anchor.service';
import { DestinationTypeV2 as DestinationType, DeIdentificationProfileDto } from '../../../../models/destination-configuration-v2.model';
import { TransformationRulesService, TransformationRule, TransformNodeSchema, TransformScope } from './transformation-rules.service';
import { RuleConfigFormComponent, applyNodeDefaults, isConfigFieldVisible } from './rule-config-form/rule-config-form.component';

interface NewDeIdRuleDraft {
  resource: string;
  sourceField: string;
  config: Record<string, string>;
}

/** One entry in the de-identification rule's "Source field" picker — see deIdFieldOptions(). `mapped` is
 *  false only for a rule's own stored field that the mapping no longer covers, kept so Edit can show it. */
interface DeIdFieldOption {
  value: string;
  label: string;
  mapped: boolean;
}

const STD_DELIMITERS = [',', '|', ';'];
type InstanceType = MappingInstanceSelection['type'];

interface NewMappingDraft {
  resource: string;
  tableName: string;
  mode: 'value' | 'childJson';
  sourcePaths: string[];
  childNodeId: string;
  targetName: string;
  instanceType: MappingInstanceSelection['type'];
}

function collectGroups(node: FmTreeNode, out: FmTreeNode[] = []): FmTreeNode[] {
  if (node.kind === 'group') {
    out.push(node);
    node.children.forEach(c => collectGroups(c, out));
  }
  return out;
}

/**
 * Collapsible bottom panel — a tabular view of every mapping AND the fully keyboard-operable
 * alternative to dragging: "+ Add mapping" builds a row from plain <select>s, no pointer input
 * required. Edits the exact same MappingRow[] the canvas renders (one state, two views).
 *
 * A resource can target more than one table (its primary table plus any added extra/child tables),
 * so the draft form has its own "Target table" picker, and every row/lookup key is
 * (resource, tableName, column) — matching the canvas's own row identity.
 */
@Component({
  selector: 'app-field-mapping-list',
  standalone: true,
  imports: [RuleConfigFormComponent],
  templateUrl: './field-mapping-list.component.html',
  styleUrl: './field-mapping-list.component.scss',
})
export class FieldMappingListComponent {
  // Same injector subtree as FieldMappingCanvasComponent (which provides this) — reused here purely to
  // read the pan/zoom viewport's live height, so this panel's resize can be clamped against the real
  // total space it shares with the canvas, not a guessed constant.
  private readonly anchors = inject(FieldMappingAnchorService);
  private readonly rulesService = inject(TransformationRulesService);

  /** Every mapping across the whole destination, not just the resource currently being edited — see
   *  visibleRows for the resource-scoped list this panel actually displays. */
  readonly rows = input.required<MappingRow[]>();
  /** Scoped to whichever single resource is currently being edited (see DestinationWizardComponent's
   *  activeMappingGroup) — used for the "+ Add mapping" draft form's own resource picker, and to scope
   *  which of `rows` this panel actually shows (see visibleRows). NOT what the reference-lookup
   *  "Resolves to" picker should use; see allResources below. */
  readonly resources = input.required<string[]>();
  /** What this panel actually renders — `rows` scoped down to the resource(s) currently being edited, so
   *  e.g. Practitioner's mappings don't show up while mapping Patient just because they share a
   *  destination. */
  readonly visibleRows = computed(() => {
    const scope = new Set(this.resources());
    return this.rows().filter(r => scope.has(r.resource));
  });

  // ── search — filters visibleRows by source field(s) or destination, same shared list/template for
  // both CSV and SQL destinations (this component has no destType-specific logic to begin with). ──────
  readonly searchQuery = signal('');
  readonly displayedRows = computed(() => {
    const q = this.searchQuery().trim().toLowerCase();
    if (!q) return this.visibleRows();
    return this.visibleRows().filter(row =>
      this.sourceSummary(row).toLowerCase().includes(q) ||
      `${row.tableName}.${row.targetName}`.toLowerCase().includes(q)
    );
  });

  onSearchInput(value: string): void { this.searchQuery.set(value); }
  clearSearch(): void { this.searchQuery.set(''); }
  /** Every resource selected for this destination, unscoped by which one is currently active — what the
   *  "Resolves to" picker offers, so a reference field on (say) Encounter can still point at Patient even
   *  while only Encounter is the resource being edited. */
  readonly allResources = input<string[]>([]);
  readonly forest = input.required<FmTreeNode[]>();
  /** All tables (primary + extras) a resource currently targets — populates the draft's table picker. */
  readonly tablesForResource = input.required<(resource: string) => string[]>();
  readonly columnsFor = input.required<(resource: string, tableName: string) => string[]>();
  readonly targetByResource = input.required<Record<string, string>>();
  readonly isApproximated = input.required<(row: MappingRow) => boolean>();

  readonly addRow = output<MappingRow>();
  readonly removeRow = output<{ resource: string; tableName: string; targetName: string }>();
  readonly editRow = output<{ resource: string; tableName: string; targetName: string; invoker: HTMLElement }>();
  /** Same DestinationType the join popover uses to load/save a per-connector rule — see
   *  FieldMappingCanvasComponent's own doc comment. Null hides the "Transformations" tab entirely (nothing
   *  to look rules up against). */
  readonly rulesDestinationType = input<DestinationType | null>(null);
  /** Bumped by FieldMappingCanvasComponent every time the join popover closes — see its own doc comment.
   *  Not read directly, just a dependency the ruleByRowKey effect below needs to re-run on, since a
   *  transformation rule can change without any MappingRow (this component's real state) changing at all. */
  readonly ruleRefreshTrigger = input<number>(0);

  // ── "De-identification" tab — shares state with DestinationWizardComponent's Step 1 picker (same
  // signals, two UI surfaces) rather than tracking its own copy, so a profile picked/created here or on
  // Step 1 is immediately visible on both. ──────────────────────────────────────────────────────────────
  readonly deIdentificationProfiles = input<DeIdentificationProfileDto[]>([]);
  readonly selectedDeIdentificationProfileId = input<string | null>(null);
  readonly newProfileName = input<string>('');
  readonly creatingProfile = input<boolean>(false);
  readonly selectedDeIdentificationProfileIdChange = output<string | null>();
  readonly newProfileNameChange = output<string>();
  readonly createDeIdentificationProfileRequested = output<void>();

  /** Display-only lookup for the "policy selected" status line — no new state, just resolves the id
   *  already tracked above to the name already present in the list already loaded for the dropdown. */
  readonly selectedProfileName = computed(
    () => this.deIdentificationProfiles().find(p => p.id === this.selectedDeIdentificationProfileId())?.name ?? ''
  );
  /** Inline edits from this row's own delimiter/instance controls — no popover required, mirroring the
   *  reference mockup's bottom panel. */
  readonly delimiterChanged = output<{ resource: string; tableName: string; targetName: string; delimiter: string }>();
  readonly instanceChanged = output<{ resource: string; tableName: string; targetName: string; instance: MappingInstanceSelection }>();
  /** Emitted when the user picks (or clears) which OTHER resource a reference field resolves against —
   *  referencesResource is null to clear it back to "written verbatim". */
  readonly referenceResourceChanged = output<{ resource: string; tableName: string; targetName: string; referencesResource: string | null }>();

  // Auto-hidden on entering Map Fields — the canvas gets the full height by default; toggleCollapsed()
  // (the existing "Mapping list" header button) still opens it on demand.
  readonly collapsed = signal(true);
  readonly draft = signal<NewMappingDraft | null>(null);

  toggleCollapsed(): void { this.collapsed.update(v => !v); }

  // ── "Transformations" tab — flat view of every connector's attached rule, alongside "Mappings" ──────
  readonly activeTab = signal<'mappings' | 'transformations' | 'deidentification'>('mappings');
  switchTab(tab: 'mappings' | 'transformations' | 'deidentification'): void { this.activeTab.set(tab); }
  /** Which tab to land on when this list first renders — set from the V2 chain node that opened the
   *  wizard (Mapping / Transformation / De-identification node). Applied as a starting value only, so
   *  switching tabs by hand still works normally. */
  readonly initialListTab = input<'mappings' | 'transformations' | 'deidentification'>('mappings');

  /** The workflow being edited — the same input the join popover takes, and for the same reason: V2 authors
   *  its rules at Workflow scope, a tier EffectiveRuleResolver only queries when it is given a route id.
   *  Without it this tab resolved the tenant-wide tiers alone and showed "no rule" for every field, while
   *  opening that field's connector (which does pass it) showed the rule right there. */
  readonly workflowId = input<string | null>(null);

  /** ruleByRowKey (see rowKey) for every 'value'-mode row currently visible — refetched whenever the
   *  visible rows or destination type change. Same getEffectiveRules lookup the join popover uses per
   *  connector, just batched here across the whole resource so this tab doesn't need to open each
   *  connector individually to see what's already configured. undefined while loading, null = no rule. */
  readonly ruleByRowKey = signal<Map<string, TransformationRule | null>>(new Map());

  constructor() {
    // Land on the tab the V2 chain node asked for (see initialListTab).
    effect(() => {
      const tab = this.initialListTab();
      untracked(() => this.activeTab.set(tab));
    });

    effect(() => {
      const destinationType = this.rulesDestinationType();
      this.ruleRefreshTrigger(); // dependency only — see its own doc comment
      const rows = this.visibleRows().filter(r => r.mode === 'value');
      if (!destinationType || rows.length === 0) {
        this.ruleByRowKey.set(new Map());
        return;
      }

      for (const row of rows) {
        const key = this.rowKey(row);
        this.rulesService
          .getEffectiveRules({
            destinationType,
            resourceType: row.resource,
            destinationField: row.targetName,
            sourceField: row.sources[0]?.fhirPath ?? null,
            // These three must stay identical to the join popover's own loadRuleFor lookup: this tab and that
            // popover answer the same question about the same field, so any divergence shows up directly as
            // "the tab says there's no rule, the connector says there is".
            resourcePipelineRouteId: this.workflowId() ?? undefined,
            workflowScopedOnly: true,
            includePending: true,
          })
          .subscribe({
            next: rules => this.ruleByRowKey.update(m => new Map(m).set(key, rules[0] ?? null)),
            error: () => this.ruleByRowKey.update(m => new Map(m).set(key, null)),
          });
      }
    });

    this.rulesService.getNodeSchemas().subscribe(schemas =>
      this.deIdSchema.set(schemas.find(s => s.nodeType === 'HashingMasking')));

    effect(() => {
      const profileId = this.selectedDeIdentificationProfileId();
      if (!profileId) {
        this.deIdRules.set([]);
        return;
      }
      this.deIdRules.set(undefined);
      // No deIdentificationProfileId query param on the list endpoint (it was never built to filter by
      // profile) — client-side filter over the full rule list, same workaround
      // DestinationWizardComponent.profileHasActiveRules already uses.
      this.rulesService.list({}).subscribe({
        next: rules => this.deIdRules.set(rules.filter(r => r.deIdentificationProfileId === profileId)),
        error: () => this.deIdRules.set([]),
      });
    });
  }

  /** displayedRows filtered to 'value'-mode rows only — childJson rows have no single sourceField the
   *  same way, matching the join popover's own d.mode !== 'childJson' guard on the transform section.
   *  The table lists every one of these (so a field with no rule yet still shows "Add rule…") — this is
   *  NOT the tab's own count; see appliedRuleCount for "how many actually have a rule attached". */
  readonly transformableRows = computed(() => this.displayedRows().filter(r => r.mode === 'value'));

  /** How many of visibleRows() (not just the search-filtered/displayed subset) currently have a real
   *  transformation rule attached — the number the "Transformations" tab badge shows. Deliberately not
   *  transformableRows().length, which is every mappable field regardless of whether any of them has a
   *  rule at all — that count answers "how many fields could have a rule", not "how many actually do". */
  readonly appliedRuleCount = computed(() =>
    this.visibleRows()
      .filter(r => r.mode === 'value')
      .filter(r => !!this.ruleByRowKey().get(this.rowKey(r)))
      .length,
  );

  ruleFor(row: MappingRow): TransformationRule | null | undefined {
    return this.ruleByRowKey().get(this.rowKey(row));
  }

  /** Short "key = value, key = value" preview of a rule's config, for the table cell — the full editor
   *  lives in the join popover this row's "Configure…" action opens. */
  /** Config as "key = value" pairs, minus any key that doesn't apply to the rule's current mode. Rules
   *  saved before those keys were scoped still carry them (e.g. "mode = hash, keepLength = 4"), and showing
   *  them reads as though they take effect — filtering here corrects the display without rewriting stored
   *  rows; re-saving a rule drops them for real (see pruneInapplicableConfig). */
  ruleConfigSummary(rule: TransformationRule): string {
    // Only the de-identification schema is loaded here, so a transformation rule of another node type is
    // shown unfiltered rather than guessed at.
    const schema = this.deIdSchema()?.nodeType === rule.nodeType ? this.deIdSchema() : undefined;
    const entries = Object.entries(rule.config)
      .filter(([key]) => {
        const field = schema?.fields.find(f => f.key === key);
        return !field || isConfigFieldVisible(field, rule.config);
      });
    return entries.length ? entries.map(([k, v]) => `${k} = ${v}`).join(', ') : '—';
  }

  onConfigureRule(row: MappingRow, ev: MouseEvent): void {
    this.editRow.emit({
      resource: row.resource, tableName: row.tableName, targetName: row.targetName,
      invoker: ev.currentTarget as HTMLElement,
    });
  }

  removeRuleQuick(row: MappingRow): void {
    const rule = this.ruleFor(row);
    if (!rule) return;
    const key = this.rowKey(row);
    this.rulesService.delete(rule.id).subscribe({
      next: () => this.ruleByRowKey.update(m => new Map(m).set(key, null)),
    });
  }

  // ── "De-identification" tab — PreMapping rules (raw source path, before any column mapping exists)
  // belonging to whichever profile is currently selected. Node type is always HashingMasking here — same
  // simplification the Transformation Rules screen's own PreMapping flow already uses (addStep() there
  // hardcodes it too), since redaction/masking/generalization is the only thing a PreMapping rule is for
  // in practice. ────────────────────────────────────────────────────────────────────────────────────────
  readonly deIdRules = signal<TransformationRule[] | undefined>(undefined); // undefined = loading
  readonly deIdSchema = signal<TransformNodeSchema | undefined>(undefined);
  readonly deIdDraft = signal<NewDeIdRuleDraft | null>(null);
  readonly deIdEditingId = signal<string | null>(null);
  readonly deIdSaving = signal(false);

  onProfileSelectChange(value: string): void {
    this.selectedDeIdentificationProfileIdChange.emit(value || null);
  }

  onNewProfileNameInput(value: string): void {
    this.newProfileNameChange.emit(value);
  }

  requestCreateProfile(): void {
    this.createDeIdentificationProfileRequested.emit();
  }

  startDeIdDraft(): void {
    this.deIdEditingId.set(null);
    this.deIdDraft.set({
      resource: this.resources()[0] ?? '',
      sourceField: '',
      config: applyNodeDefaults(this.deIdSchema(), {}),
    });
  }

  /**
   * Fields offerable to a de-identification rule: only those this resource actually maps, not every leaf in
   * the payload tree. Redacting an unmapped field is a no-op for a column-based destination — the value is
   * never written anywhere — so listing all 184 leaves was offering ~178 choices that silently do nothing.
   * (This panel is only rendered for column-based destinations; whole-resource FHIR destinations, where every
   * field IS delivered and this filter would be wrong, take their own Step 3 branch and never reach here.)
   *
   * `currentSourceField` is kept in the list even when it is no longer mapped, so opening Edit on an older
   * rule shows its field rather than a blank select that would silently rewrite the rule on save.
   */
  deIdFieldOptions(resource: string, currentSourceField: string): DeIdFieldOption[] {
    const rows = this.rows().filter(row => row.resource === resource);

    const mappedPaths = new Set<string>();
    const childRoots: string[] = [];
    for (const row of rows) {
      if (row.mode === 'childJson') {
        // A childJson row maps a whole subtree into one column, so every leaf beneath it is delivered.
        if (row.childNodeId) childRoots.push(row.childNodeId);
        continue;
      }
      for (const source of row.sources) mappedPaths.add(source.fhirPath);
    }

    const options = this.leavesFor(resource)
      .filter(leaf =>
        mappedPaths.has(leaf.id)
        || childRoots.some(root => leaf.id === root || leaf.id.startsWith(root + '.')))
      .map(leaf => ({ value: leaf.field?.jsonPath ?? leaf.id, label: leaf.label, mapped: true }));

    if (currentSourceField && !options.some(option => option.value === currentSourceField)) {
      options.unshift({ value: currentSourceField, label: `${currentSourceField} (no longer mapped)`, mapped: false });
    }

    return options;
  }

  /** Loads an existing rule for editing regardless of its original scope — a rule authored elsewhere
   *  (Settings screen, or before this quick-add form always scoped to ResourceType) can still be Global.
   *  Re-saving it from here always writes it back as ResourceType, matching this form's "no scope choice"
   *  simplification — editing a Global rule here narrows it to this resource going forward. */
  editDeIdRule(rule: TransformationRule): void {
    this.deIdEditingId.set(rule.id);
    this.deIdDraft.set({
      resource: rule.resourceType ?? this.resources()[0] ?? '',
      sourceField: rule.sourceField ?? '',
      config: { ...rule.config },
    });
  }

  cancelDeIdDraft(): void {
    this.deIdDraft.set(null);
    this.deIdEditingId.set(null);
  }

  updateDeIdDraftResource(resource: string): void {
    this.deIdDraft.update(d => (d ? { ...d, resource, sourceField: '' } : d));
  }

  updateDeIdDraftSourceField(sourceField: string): void {
    this.deIdDraft.update(d => (d ? { ...d, sourceField } : d));
  }

  readonly canSubmitDeIdDraft = computed(() => {
    const d = this.deIdDraft();
    return !!d && !!d.sourceField && !!d.resource;
  });

  submitDeIdDraft(): void {
    const d = this.deIdDraft();
    const profileId = this.selectedDeIdentificationProfileId();
    if (!d || !profileId || !this.canSubmitDeIdDraft()) return;

    this.deIdSaving.set(true);
    this.rulesService
      .save({
        id: this.deIdEditingId(),
        scope: 'ResourceType' as TransformScope,
        nodeType: 'HashingMasking',
        config: d.config,
        resourceType: d.resource,
        sourceField: d.sourceField,
        order: 0,
        onNull: 'Skip',
        errorPolicy: 'NullOut',
        isEnabled: true,
        arrayMode: 'Whole',
        executionPhase: 'PreMapping',
        deIdentificationProfileId: profileId,
      })
      .subscribe({
        next: saved => {
          this.deIdSaving.set(false);
          this.deIdRules.update(rules =>
            rules ? [...rules.filter(r => r.id !== saved.id), saved] : [saved]);
          this.cancelDeIdDraft();
        },
        error: () => this.deIdSaving.set(false),
      });
  }

  removeDeIdRule(rule: TransformationRule): void {
    this.rulesService.delete(rule.id).subscribe({
      next: () => this.deIdRules.update(rules => rules?.filter(r => r.id !== rule.id) ?? []),
    });
  }

  // ── drag-to-resize this panel's whole height (handle + head + body) — the boundary between the
  // canvas above and this list. The canvas's own min-height (.fm-viewport CSS) MUST match
  // MIN_CANVAS_HEIGHT below, or the two clamps disagree and either overflow or leave a gap. ──
  private static readonly MIN_PANEL_HEIGHT = 150;
  private static readonly MIN_CANVAS_HEIGHT = 150;
  readonly panelHeight = signal(260);

  @HostBinding('style.height.px') get hostHeight(): number | null {
    return this.collapsed() ? null : this.panelHeight();
  }

  private resizeStart: { pointerId: number; startClientY: number; startHeight: number; totalAvailable: number } | null = null;

  onResizeHandlePointerDown(ev: PointerEvent): void {
    if (ev.button !== 0) return;
    // Captured once per drag, not read live on every move — the canvas viewport's own height changes
    // in lockstep with this panel's (both are flex siblings under one fixed-height parent), so re-reading
    // it mid-drag would just be reading back the effect of this same drag instead of the fixed total.
    this.resizeStart = {
      pointerId: ev.pointerId,
      startClientY: ev.clientY,
      startHeight: this.panelHeight(),
      totalAvailable: this.anchors.viewportSize().height + this.panelHeight(),
    };
    (ev.currentTarget as HTMLElement).setPointerCapture(ev.pointerId);
  }

  onResizeHandlePointerMove(ev: PointerEvent): void {
    if (!this.resizeStart || ev.pointerId !== this.resizeStart.pointerId) return;
    // Dragging the handle down grows the canvas above (and shrinks this list); dragging up grows this
    // list (and shrinks the canvas) — the handle sits at this panel's own top edge.
    const delta = ev.clientY - this.resizeStart.startClientY;
    const next = this.resizeStart.startHeight - delta;
    const maxHeight = this.resizeStart.totalAvailable - FieldMappingListComponent.MIN_CANVAS_HEIGHT;
    this.panelHeight.set(Math.max(
      FieldMappingListComponent.MIN_PANEL_HEIGHT,
      Math.min(maxHeight, next),
    ));
  }

  onResizeHandlePointerUp(ev: PointerEvent): void {
    if (!this.resizeStart || ev.pointerId !== this.resizeStart.pointerId) return;
    const el = ev.currentTarget as HTMLElement;
    if (el.hasPointerCapture(ev.pointerId)) el.releasePointerCapture(ev.pointerId);
    this.resizeStart = null;
  }

  rowKey(row: MappingRow): string { return `${row.resource}::${row.tableName}::${row.targetName}`; }

  sourceSummary(row: MappingRow): string {
    return row.mode === 'childJson'
      ? `${row.childNodeId} (whole node → JSON)`
      : row.sources.map(s => s.fhirPath).join(row.sources.length > 1 ? ` + ` : '');
  }

  modeLabel(row: MappingRow): string {
    if (row.mode === 'childJson') return 'Whole node → JSON';
    return row.sources.length > 1 ? `Joined ×${row.sources.length}` : 'Direct';
  }

  // ── inline delimiter / instance-selection controls ──────────────────────────────────────────────
  readonly stdDelimiters = STD_DELIMITERS;

  showsDelimiter(row: MappingRow): boolean { return row.mode === 'value' && row.sources.length > 1; }

  /** Whether this row sits under a repeating source (childJson's own node, or the leaf's nearest
   *  enclosing array group) — showing "which instance?" only makes sense when there's something to pick
   *  an instance of. */
  hasArrayAncestor(row: MappingRow): boolean {
    const startId = row.mode === 'childJson' ? row.childNodeId : row.sources[0]?.fhirPath;
    if (!startId) return false;
    const root = this.forest().find(r => r.resource === row.resource);
    return !!root && nearestArrayGroupId(this.forest(), startId) !== null;
  }

  isCustomDelimiter(row: MappingRow): boolean {
    return !STD_DELIMITERS.includes(row.delimiter ?? ',');
  }

  onDelimiterSelectChange(row: MappingRow, value: string): void {
    if (value === '__custom') return; // the custom text input drives the actual change
    this.delimiterChanged.emit({ resource: row.resource, tableName: row.tableName, targetName: row.targetName, delimiter: value });
  }

  onDelimiterCustomInput(row: MappingRow, value: string): void {
    this.delimiterChanged.emit({ resource: row.resource, tableName: row.tableName, targetName: row.targetName, delimiter: value || ',' });
  }

  onInstanceTypeChange(row: MappingRow, type: InstanceType): void {
    this.instanceChanged.emit({ resource: row.resource, tableName: row.tableName, targetName: row.targetName, instance: { ...row.instance, type } });
  }

  onInstanceNChange(row: MappingRow, n: number): void {
    this.instanceChanged.emit({
      resource: row.resource, tableName: row.tableName, targetName: row.targetName,
      instance: { ...row.instance, type: 'nth', n: n || 1 },
    });
  }

  onInstanceCriteriaChange(row: MappingRow, field: string, op: MappingInstanceSelection['op'], value: string): void {
    this.instanceChanged.emit({
      resource: row.resource, tableName: row.tableName, targetName: row.targetName,
      instance: { ...row.instance, type: 'criteria', field, op, value },
    });
  }

  // ── reference-lookup control — any single-value mapped field, see isReferenceCandidate ──
  isReferenceCandidate = isReferenceCandidate;

  /** Every mapped resource this row could resolve against — excludes its own resource (a reference never
   *  points at its own resource type in these mappings). Drawn from allResources (every resource selected
   *  for this destination), not the narrower `resources` input (just the one currently being edited) —
   *  otherwise this list would always come back empty except while editing a resource alongside itself. */
  otherResources(row: MappingRow): string[] {
    return this.allResources().filter(r => r !== row.resource);
  }

  onReferenceResourceChange(row: MappingRow, value: string): void {
    this.referenceResourceChanged.emit({
      resource: row.resource, tableName: row.tableName, targetName: row.targetName,
      referencesResource: value || null,
    });
  }

  onRemove(row: MappingRow): void {
    this.removeRow.emit({ resource: row.resource, tableName: row.tableName, targetName: row.targetName });
  }

  onEdit(row: MappingRow, ev: MouseEvent): void {
    this.editRow.emit({
      resource: row.resource, tableName: row.tableName, targetName: row.targetName,
      invoker: ev.currentTarget as HTMLElement,
    });
  }

  // ── "+ Add mapping" draft form ───────────────────────────────────────────
  startDraft(): void {
    const resource = this.resources()[0] ?? '';
    const tableName = this.tablesForResource()(resource)[0] ?? '';
    this.draft.set({ resource, tableName, mode: 'value', sourcePaths: [''], childNodeId: '', targetName: '', instanceType: 'all' });
  }

  cancelDraft(): void { this.draft.set(null); }

  leavesFor(resource: string): FmTreeNode[] {
    const root = this.forest().find(r => r.resource === resource);
    return root ? flattenLeaves(root) : [];
  }

  groupsFor(resource: string): FmTreeNode[] {
    const root = this.forest().find(r => r.resource === resource);
    return root ? collectGroups(root) : [];
  }

  updateDraftResource(resource: string): void {
    const tableName = this.tablesForResource()(resource)[0] ?? '';
    this.draft.update(d => (d ? { ...d, resource, tableName, sourcePaths: [''], childNodeId: '', targetName: '' } : d));
  }

  updateDraftTable(tableName: string): void {
    this.draft.update(d => (d ? { ...d, tableName, targetName: '' } : d));
  }

  updateDraftMode(mode: 'value' | 'childJson'): void {
    this.draft.update(d => (d ? { ...d, mode } : d));
  }

  updateDraftSourcePath(i: number, path: string): void {
    this.draft.update(d => {
      if (!d) return d;
      const sourcePaths = [...d.sourcePaths];
      sourcePaths[i] = path;
      return { ...d, sourcePaths };
    });
  }

  addDraftSource(): void {
    this.draft.update(d => (d ? { ...d, sourcePaths: [...d.sourcePaths, ''] } : d));
  }

  removeDraftSource(i: number): void {
    this.draft.update(d => (d ? { ...d, sourcePaths: d.sourcePaths.filter((_, idx) => idx !== i) } : d));
  }

  updateDraftChildNode(id: string): void {
    this.draft.update(d => (d ? { ...d, childNodeId: id } : d));
  }

  updateDraftTarget(targetName: string): void {
    this.draft.update(d => (d ? { ...d, targetName } : d));
  }

  updateDraftInstanceType(instanceType: MappingInstanceSelection['type']): void {
    this.draft.update(d => (d ? { ...d, instanceType } : d));
  }

  readonly canSubmitDraft = computed(() => {
    const d = this.draft();
    if (!d || !d.resource || !d.tableName || !d.targetName) return false;
    return d.mode === 'childJson' ? !!d.childNodeId : d.sourcePaths.every(p => !!p);
  });

  submitDraft(): void {
    const d = this.draft();
    if (!d || !this.canSubmitDraft()) return;

    if (d.mode === 'childJson') {
      this.addRow.emit({
        resource: d.resource, sources: [], mode: 'childJson', childNodeId: d.childNodeId,
        targetName: d.targetName, tableName: d.tableName,
      });
    } else {
      const leaves = this.leavesFor(d.resource);
      const sources = d.sourcePaths.map(path => {
        const leaf = leaves.find(l => l.id === path);
        return {
          fhirPath: path,
          label: leaf?.label ?? path,
          jsonPath: leaf?.field?.jsonPath,
          valueType: leaf?.field?.valueType,
          arrays: leaf?.field?.arrays,
        };
      });
      this.addRow.emit({
        resource: d.resource, sources, mode: 'value',
        delimiter: sources.length > 1 ? ', ' : undefined,
        instance: { type: d.instanceType },
        targetName: d.targetName, tableName: d.tableName,
      });
    }
    this.draft.set(null);
  }
}
