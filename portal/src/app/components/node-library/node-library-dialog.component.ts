import { Component, input, output, inject, computed, signal, effect, untracked, viewChild } from '@angular/core';
import { NgComponentOutlet } from '@angular/common';
import { ModalOverlayComponent } from '../shared/modal-overlay/modal-overlay.component';
import { PipelineStore } from '../../services/pipeline.store';
import { ApplicabilityService } from '../../services/applicability.service';
import { PhaseConfigService } from '../../services/phase-config.service';
import { WizardService } from '../../services/wizard.service';
import { WorkflowGraphMapperService } from '../../services/workflow-graph-mapper.service';
import { PermissionService } from '../../auth/services/permission.service';
import { ToastService } from '../../services/toast.service';
import { SOURCES } from '../../data/sources.data';
import { TRANSFORMS } from '../../data/transforms.data';
import { RANK_LABEL } from '../../models/transform.model';
import { CanvasNode, SourceNode, TransformNode, isSourceNode } from '../../models/node.model';
import { MergeNodeOption } from '../../models/wizard-state.model';
import { SourceConfigFormComponent } from '../shared/config-form/config-form.contract';
import { SOURCE_FORM_REGISTRY, EHR_VENDOR_TO_SOURCE_FORM_KEY, SELF_CONTAINED_SOURCE_FORM_KEYS } from './source-form.registry';
import { sourceFormKeyForNode } from './source-node-vendor.util';
import { EpicSourceFormComponent } from './epic-source-form/epic-source-form.component';
import { CernerSourceFormComponent } from './cerner-source-form/cerner-source-form.component';
import { AthenahealthSourceFormComponent } from './athenahealth-source-form/athenahealth-source-form.component';
import { AllscriptsSourceFormComponent } from './allscripts-source-form/allscripts-source-form.component';
import { HealowSourceFormComponent } from './healow-source-form/healow-source-form.component';
import { MeditechSourceFormComponent } from './meditech-source-form/meditech-source-form.component';
import { SampleSourceFormComponent } from './sample-source-form/sample-source-form.component';
import { DestinationWizardComponent } from './destination-wizard/destination-wizard.component';
import { ZoomDockComponent } from '../canvas/zoom-dock/zoom-dock.component';

// SELF_CONTAINED_SOURCE_FORM_KEYS (imported above) distinguishes the WizardService-backed vendor forms (Epic,
// Cerner, ...) — each with its own full save/cancel flow and topbar/footer chrome, exactly like the old
// (Epic-only) EpicAudienceFormComponent — from "headless" forms (generic-fhir, hl7v2) that only implement
// SourceConfigFormComponent.getFields() and rely on this dialog's own title/Cancel/Save chrome, the same shape
// GenericFhirSourceFormComponent always used. This split exists because Angular's NgComponentOutlet has no way to
// bind a dynamically-resolved component's *outputs* in a template (only inputs, since v17) — the self-contained
// vendor forms are rendered through a `@switch` below instead so their saved/cancelled/closeAll/
// toggleMaximizeRequest outputs can stay ordinary, statically-checked Angular bindings.

export type LibraryMode = 'source' | 'transform';

export interface AddTransformEvent {
  attachNode: CanvasNode;
  transformId: string;
  status: string;
  config?: Record<string, string>;
  editNodeId?: string;
}

export interface MergeEvent {
  opt: MergeNodeOption;
}

type ItemStatus = 'enabled' | 'show' | 'caveat' | 'hide' | 'disabled';

interface LibraryItem {
  id: string;
  rank: number;
  name: string;
  sub: string;
  abbr: string;
  color: string;
  category: string | null;
  isSource: boolean;
  status: ItemStatus;
  reason?: string | null;
  group?: string | null;
  isMerge?: boolean;
  mergeOpt?: MergeNodeOption;
}

interface LibraryCategory {
  rank: number;
  label: string;
  icon: string;
  catColor: string;
  items: LibraryItem[];
}

const TRANSFORM_META: Record<string, { abbr: string; color: string }> = {
  'fhir-validation':  { abbr: 'VAL', color: '#10B981' },
  'normalize':        { abbr: 'NRM', color: '#3B82F6' },
  'patient-matching': { abbr: 'MPI', color: '#3B82F6' },
  'merge-patients':   { abbr: 'MRG', color: '#3B82F6' },
  'terminology':      { abbr: 'TRM', color: '#F59E0B' },
  'deid-safeharbor':  { abbr: 'DEI', color: '#EF4444' },
  'deid-kanon':       { abbr: 'KAN', color: '#EF4444' },
  'field-mapping':    { abbr: 'MAP', color: '#6366F1' },
  'dest-sqlserver':   { abbr: 'SQL', color: '#CC2927' },
  'dest-azuresql':    { abbr: 'AZS', color: '#0078D4' },
  'dest-postgres':    { abbr: 'PG',  color: '#336791' },
  'dest-mysql':       { abbr: 'MY',  color: '#4479A1' },
  'dest-mongo':       { abbr: 'MDB', color: '#47A248' },
  'dest-snowflake':   { abbr: 'SNW', color: '#29B5E8' },
  'dest-powerbi':     { abbr: 'PBI', color: '#F2C811' },
  'dest-tableau':     { abbr: 'TAB', color: '#E97627' },
  'dest-databricks':  { abbr: 'DBR', color: '#FF3621' },
  'dest-blob':        { abbr: 'BLB', color: '#0089D6' },
  'dest-s3':          { abbr: 'S3',  color: '#FF9900' },
  'dest-fhir':        { abbr: 'AB',  color: '#00A89D' },
  'dest-medplum':     { abbr: 'MP',  color: '#00A89D' },
  'dest-azurefhir':   { abbr: 'AZF', color: '#0078D4' },
  'dest-csv':         { abbr: 'CSV', color: '#374151' },
  'dest-xlsx':        { abbr: 'XLS', color: '#217346' },
  'dest-ndjson':      { abbr: 'NDJ', color: '#475569' },
  'dest-parquet':     { abbr: 'PAR', color: '#64748B' },
  'dest-avro':        { abbr: 'AVR', color: '#64748B' },
  'dest-protobuf':    { abbr: 'PRT', color: '#64748B' },
  'dest-pdf':         { abbr: 'PDF', color: '#DC2626' },
  'dest-sftp':        { abbr: 'FTP', color: '#475569' },
  'dest-restapi':     { abbr: 'API', color: '#475569' },
  'dest-inmemory':    { abbr: 'MEM', color: '#94A3B8' },
  'audit-lineage':    { abbr: 'AUD', color: '#0EA5E9' },
  'hedis':            { abbr: 'HDI', color: '#D946EF' },
  'anomaly':          { abbr: 'ANO', color: '#D946EF' },
  'patient-agg':      { abbr: 'AGG', color: '#D946EF' },
};

const RANK_META: Record<number, { icon: string; catColor: string }> = {
  0: { icon: '⬡', catColor: '#00A89D' },
  1: { icon: '⊙', catColor: '#8B5CF6' },
  2: { icon: '✓', catColor: '#10B981' },
  3: { icon: '⇄', catColor: '#3B82F6' },
  4: { icon: '⌘', catColor: '#F59E0B' },
  5: { icon: '⊘', catColor: '#EF4444' },
  6: { icon: '⚡', catColor: '#6366F1' },
  7: { icon: '▶', catColor: '#64748B' },
  8: { icon: '◎', catColor: '#0EA5E9' },
  9: { icon: '◈', catColor: '#D946EF' },
};

@Component({
  selector: 'app-node-library-dialog',
  standalone: true,
  imports: [
    ModalOverlayComponent,
    NgComponentOutlet,
    EpicSourceFormComponent,
    CernerSourceFormComponent,
    AthenahealthSourceFormComponent,
    AllscriptsSourceFormComponent,
    HealowSourceFormComponent,
    MeditechSourceFormComponent,
    SampleSourceFormComponent,
    DestinationWizardComponent,
    ZoomDockComponent,
  ],
  templateUrl: './node-library-dialog.component.html',
  styleUrl: './node-library-dialog.component.scss',
})
export class NodeLibraryDialogComponent {
  private readonly store    = inject(PipelineStore);
  private readonly appSvc   = inject(ApplicabilityService);
  private readonly phaseCfg = inject(PhaseConfigService);
  private readonly graphMapper = inject(WorkflowGraphMapperService);
  private readonly permissions = inject(PermissionService);
  private readonly toast = inject(ToastService);
  readonly wiz              = inject(WizardService);

  readonly open         = input(false);
  readonly mode         = input<LibraryMode>('source');
  readonly originNodeId = input<string | null>(null);
  readonly editNodeId   = input<string | null>(null);

  readonly closed            = output<void>();
  readonly sourceSelected    = output<string>();
  readonly transformSelected = output<AddTransformEvent>();
  readonly mergeSelected     = output<MergeEvent>();

  // ── local UI state ────────────────────────────────────────────────────────
  readonly searchQuery   = signal('');
  readonly selectedId    = signal<string | null>(null);
  readonly showHidden    = signal(false);

  // ── inline source-config form state ───────────────────────────────────────
  /** SOURCES catalog id (see ../../data/sources.data.ts) of the currently-open inline source form, or null when
   *  none is open — the single piece of state SOURCE_FORM_REGISTRY is keyed off of. Replaces the old
   *  showEpicForm/showGenericFhirForm boolean pair; adding a new source type never means a new boolean here. */
  readonly openSourceFormType = signal<string | null>(null);
  readonly currentSourceFormComponent = computed(() => {
    const type = this.openSourceFormType();
    return type ? SOURCE_FORM_REGISTRY[type] ?? null : null;
  });
  /** Display name for the "headless" form title (see the template) — selectedItem()/selectedId() are never set
   *  for a registered source (selectItem() opens its form immediately instead), so this reads sources.data.ts
   *  directly off openSourceFormType() rather than relying on that unrelated signal. */
  readonly sourceFormTitle = computed(() =>
    SOURCES.find(s => s.id === this.openSourceFormType())?.name ?? 'Source');
  /** True for the WizardService-backed vendor forms (Epic/Cerner/.../Sample) — see SELF_CONTAINED_SOURCE_FORM_KEYS. */
  readonly isSelfContainedSourceForm = computed(() => {
    const type = this.openSourceFormType();
    return !!type && SELF_CONTAINED_SOURCE_FORM_KEYS.has(type);
  });
  /** Only meaningful for a "headless" form (generic-fhir, hl7v2) — the canvas node being edited, whose `fields`
   *  seed that form's own `initialFields` input. Self-contained vendor forms restore instead through
   *  WizardService.open()/editingFields(), same as EpicAudienceFormComponent always did. */
  readonly sourceFormEditNode = signal<CanvasNode | null>(null);
  /** Read back out via getFields() by this dialog's own Save button for a headless form (see
   *  onHeadlessSourceFormSave) — generalizes the old genericFhirForm() viewChild. */
  readonly sourceFormOutlet = viewChild(NgComponentOutlet);
  readonly sourceFormError = signal<string | null>(null);

  // ── sidebar collapsed state (auto when a form opens, user-toggleable) ─────
  readonly sidebarPinned = signal(false);
  readonly isSidebarMini = computed(() =>
    (this.openSourceFormType() !== null || this.showDestWizard()) && !this.sidebarPinned()
  );

  toggleSidebar(): void { this.sidebarPinned.update(v => !v); }

  // ── destination wizard state ──────────────────────────────────────────────
  readonly showDestWizard   = signal(false);
  readonly destWizardType   = signal<'sql' | 'csv' | 'mysql' | 'mongo' | 'postgres' | 'medplum' | 'fhir' | 'blob' | 'azurefhir' | null>(null);
  readonly destWizardAttach = signal<CanvasNode | null>(null);
  readonly destEditNode     = signal<CanvasNode | null>(null);
  // Queried directly (rather than threading another output through) so both onDestWizardCancelled() and
  // selectItem()'s switch-type guard can check isStep1Dirty() procedurally at click time — see
  // DestinationWizardComponent.isStep1Dirty() for why destWizardHasProgressed alone isn't enough.
  private readonly destWizardRef = viewChild(DestinationWizardComponent);

  // FHIR resource types the pipeline's source(s) pull — union of every source node's saved "Resources" field plus the
  // active wizard selection. Passed to the destination wizard so its data groups mirror the source's Resource Type.
  readonly sourceResources = computed(() => {
    const fromNodes = this.store.nodes()
      .filter(isSourceNode)
      .flatMap(n => (n.fields?.['Resources'] ?? '').split(',').map(s => s.trim()).filter(Boolean));
    return Array.from(new Set([...fromNodes, ...this.wiz.resources()]));
  });

  // The pipeline's launch source node's saved connection id — the Mapping JSON's per-resource
  // "sourceConnectionId" field. Reactive to store.nodes() via findLaunchSourceId()'s own read of it.
  readonly sourceConnectionId = computed(() => this.graphMapper.findLaunchSourceId());

  // FHIR resource types the source's live Discover (/metadata) probe actually returned this session — see
  // EhrVendorSourceFormComponent's 'Discovered resource types' field. Distinct from sourceResources above
  // (the admin's manually-selected retrieval resources): this is what the source can ACTUALLY provide,
  // used by the destination wizard to intersect against our own supported-resource catalog for Step 2.
  // Available as soon as the source form is saved in THIS canvas session, even before the source has ever
  // been persisted as a real SourceConnection (so before sourceConnectionId exists) — DestinationWizardComponent
  // falls back to a live re-probe via sourceConnectionId when this is empty (e.g. editing a destination on an
  // already-saved workflow whose source form hasn't been reopened this session).
  readonly sourceDiscoveredResourceTypes = computed(() => {
    const fromNodes = this.store.nodes()
      .filter(isSourceNode)
      .flatMap(n => (n.fields?.['Discovered resource types'] ?? '').split(',').map(s => s.trim()).filter(Boolean));
    return Array.from(new Set(fromNodes));
  });

  // Mirrors the open destination wizard's own step/progress so the sidebar can
  // lock the other destination type out mid-wizard and warn before discarding.
  readonly destWizardStep         = signal(1);
  readonly destWizardHasProgressed = signal(false);

  // True while the destination wizard has a specific data group's mapping canvas open — hides the
  // node-picker sidebar entirely so the canvas gets the full dialog width.
  readonly destMappingCanvasActive = signal(false);
  // "Map fields — {group}" while a group's canvas is open; null shows the normal "Node Library" title.
  readonly destMappingTitle = signal<string | null>(null);

  // Header-level Close/Save (shown in place of the maximize icon's neighboring × while a group's
  // canvas is open) trigger the wizard's own methods via an incrementing counter input, since a
  // template reference variable on <app-destination-wizard> isn't reachable from here — it's declared
  // inside a conditional @if/@else branch, out of scope for the sibling header buttons.
  readonly exitMappingTrigger = signal(0);
  readonly saveMappingTrigger = signal(0);
  bumpExitMappingTrigger(): void { this.exitMappingTrigger.update(v => v + 1); }
  bumpSaveMappingTrigger(): void { this.saveMappingTrigger.update(v => v + 1); }

  // Same counter-trigger pattern, for the independent "save a mapping-only snapshot" action — never
  // touches the workflow-level Save above (saveMappingTrigger), which still only keeps in-memory
  // progress and closes the canvas; this one persists just the mapping screen's own state.
  readonly saveSnapshotTrigger = signal(0);
  bumpSaveSnapshotTrigger(): void { this.saveSnapshotTrigger.update(v => v + 1); }

  // Same counter-trigger pattern for the canvas's "Load JSON payload"/"Preview output" actions, now
  // shown here instead of the canvas's own toolbar row (freeing that row's height for the canvas itself).
  readonly loadPayloadTrigger = signal(0);
  readonly previewOutputTrigger = signal(0);
  bumpLoadPayloadTrigger(): void { this.loadPayloadTrigger.update(v => v + 1); }
  bumpPreviewOutputTrigger(): void { this.previewOutputTrigger.update(v => v + 1); }

  // Same pattern again for "Suggest mappings"/"Clear suggestions" and the zoom-dock controls, also
  // relocated here from the canvas's own floating chrome (see field-mapping-canvas.component.html).
  readonly runSuggestMappingsTrigger = signal(0);
  readonly clearSuggestionsTrigger = signal(0);
  readonly zoomInTrigger = signal(0);
  readonly zoomOutTrigger = signal(0);
  readonly zoomResetTrigger = signal(0);
  readonly zoomFitTrigger = signal(0);
  // Same pattern for "Mark as Master" — promotes the currently-open resource's mapping into a new,
  // independently-named master template other workflows can later find via "Select Existing".
  readonly markAsMasterTrigger = signal(0);
  bumpMarkAsMasterTrigger(): void { this.markAsMasterTrigger.update(v => v + 1); }
  bumpRunSuggestMappingsTrigger(): void { this.runSuggestMappingsTrigger.update(v => v + 1); }
  bumpClearSuggestionsTrigger(): void { this.clearSuggestionsTrigger.update(v => v + 1); }
  bumpZoomInTrigger(): void { this.zoomInTrigger.update(v => v + 1); }
  bumpZoomOutTrigger(): void { this.zoomOutTrigger.update(v => v + 1); }
  bumpZoomResetTrigger(): void { this.zoomResetTrigger.update(v => v + 1); }
  bumpZoomFitTrigger(): void { this.zoomFitTrigger.update(v => v + 1); }

  // Mirrors the canvas's own suggestions().length / zoomPercent() up to this header (see
  // DestinationWizardComponent's suggestionCountChange/zoomPercentChange outputs).
  readonly destSuggestionCount = signal(0);
  readonly destZoomPercent = signal('100%');

  // Toolbar-level search box (see .nld-mapping-toolbar-search) — live text forwarded straight down
  // through DestinationWizardComponent -> FieldMappingCanvasComponent to both the payload source tree
  // and every destination table's columns, each filtering by name/path/data type. Unlike the counters
  // above this isn't a one-shot action, so there's no bump/trigger pair — just a plain read/write signal.
  readonly mappingSearchQuery = signal('');
  onMappingSearchInput(value: string): void { this.mappingSearchQuery.set(value); }
  clearMappingSearch(): void { this.mappingSearchQuery.set(''); }

  // Whether the whole dialog is expanded to near-fullscreen (see .nld--maximized) — the maximize button
  // lives in several places (mapping-canvas header, floating controls, and each form's own topbar) but
  // they all toggle this one piece of state.
  readonly isMaximized = signal(false);
  toggleMaximize(): void { this.isMaximized.update(v => !v); }

  // Mirrors the open canvas's own destination type / mapping count so the header can show them
  // without reaching into the wizard's nested-@if template (a template ref there is out of scope here).
  // Kept in sync by hand with DestinationWizardComponent.destLabel's own type->label mapping — was
  // previously a hardcoded 'sql' ? 'SQL Server' : 'CSV' binary, silently mislabeling every other real
  // destination type (MySQL, PostgreSQL, MongoDB, Blob, Medplum, FHIR, Azure FHIR) as "CSV" in this
  // header badge, even though the mapping canvas itself (destType-driven "table"/"file"/"blob" wording,
  // "delimiters" hint, etc.) was always using the correct type underneath.
  readonly destMappingTypeLabel = computed(() => {
    switch (this.destWizardType()) {
      case 'sql': return 'SQL Server';
      case 'mysql': return 'MySQL';
      case 'postgres': return 'PostgreSQL';
      case 'mongo': return 'MongoDB';
      case 'medplum': return 'Medplum';
      case 'fhir': return 'Aidbox';
      case 'azurefhir': return 'Azure FHIR Service';
      case 'blob': return 'Azure Blob Storage';
      default: return 'CSV';
    }
  });

  /** Same "was only ever SQL Server vs. everything-else" gap as destMappingTypeLabel above — a relational
   *  destination (SQL Server/MySQL/PostgreSQL) gets the database icon, FHIR-shaped destinations get a
   *  different one, blob/CSV keep the plain file icon. */
  readonly destMappingTypeIcon = computed(() => {
    switch (this.destWizardType()) {
      case 'sql': case 'mysql': case 'postgres': return '🗄️';
      case 'mongo': return '🍃';
      case 'medplum': case 'fhir': case 'azurefhir': return '🔥';
      default: return '📄';
    }
  });
  readonly destMappingCount = signal(0);

  readonly pendingDestSwitch      = signal<'sql' | 'csv' | 'mysql' | 'mongo' | 'postgres' | 'medplum' | 'fhir' | 'blob' | 'azurefhir' | null>(null);

  // Guards the dest wizard's own "← Back to library" button — same "don't silently discard progress"
  // intent as pendingDestSwitch above, just for backing out to the library instead of switching type.
  // (cancel() on the wizard itself has no notion of "has this progressed" — that lives here, driven by
  // destWizardHasProgressed — so it always emits `cancelled` unconditionally and lets this gate decide.)
  readonly pendingDestCancel = signal(false);

  // Guards the overlay's own close (Escape key, or a backdrop click on the rare occasions it's still
  // enabled) while a form/wizard is open — same "don't silently discard progress" intent as
  // pendingDestSwitch above, just for exiting the whole dialog instead of switching destination type.
  readonly pendingCloseConfirm = signal(false);

  private readonly destTypeLocked = computed(() =>
    this.showDestWizard() && this.destWizardStep() > 1
  );

  constructor() {
    // When the dialog opens with an editNodeId, jump straight into the right form.
    effect(() => {
      const id = this.editNodeId();
      if (this.open() && id) {
        const node = untracked(() => this.store.byId(id));
        if (node?.kind === 'transform') {
          const tId = (node as TransformNode).transformId;
          if (tId === 'dest-sqlserver' || tId === 'dest-csv' || tId === 'dest-mysql' || tId === 'dest-mongo' || tId === 'dest-postgres' || tId === 'dest-fhir' || tId === 'dest-blob' || tId === 'dest-medplum' || tId === 'dest-azurefhir') {
            untracked(() => this._openDestWizardEdit(node));
            // _openDestWizardEdit's own canOpenDestWizard() check already showed a toast and returned
            // without setting showDestWizard() true if the permission check failed — stopping there would
            // otherwise leave the whole dialog open on the empty "Select a node from the left" placeholder,
            // with nothing left to select from. Close it so the visible result is just the toast, not a
            // stranded dialog that opened only to immediately reject the one node it was opened for.
            if (!untracked(() => this.showDestWizard())) { untracked(() => this._close()); }
          }
          return;
        }
        // Resolve which registered source form owns this node (defaulting to 'epic' for a node with no
        // recognizable vendor marker — e.g. every canvas node created before per-vendor forms existed).
        const key = node && isSourceNode(node) ? this._sourceFormKeyForNode(node) : 'epic';
        untracked(() => this.openSourceForm(key, node ?? id));
        // Same reasoning as the destination branch above — openSourceForm() only sets
        // openSourceFormType() on success; if its permission check failed, close the dialog instead of
        // leaving it stranded on the empty picker with the toast as the only sign anything happened.
        if (!untracked(() => this.openSourceFormType())) { untracked(() => this._close()); }
      }
    });
  }

  // ── picker model (transform mode only) ───────────────────────────────────
  private readonly pickerModel = computed(() => {
    if (this.mode() !== 'transform') return null;
    const id = this.originNodeId();
    if (!id) return null;
    const node = this.store.byId(id);
    if (!node) return null;
    return this.appSvc.pickerModel(
      node,
      this.store.nodes(),
      this.store.edges(),
      this.showHidden(),
    );
  });

  readonly pickerMeta = computed(() => this.pickerModel());

  // ── all categories ────────────────────────────────────────────────────────
  private readonly allCategories = computed<LibraryCategory[]>(() => {
    const m = this.mode();
    const pm = this.pickerModel();
    const byRank = new Map<number, LibraryItem[]>();

    // Rank 0 — Sources (filtered by phase config: only enabled sources shown; and by RBAC — a source
    // with a dedicated permission group the current role lacks View for is excluded from the library
    // entirely, same as a phase-hidden source, rather than shown disabled. Tile visibility is
    // specifically the vendor's own View permission — separate from Create (gates actually adding a
    // new node, see onSourceSelected) and Edit (gates modifying an existing one) — a role could hold
    // View without Create/Edit and still see the tile, just be unable to complete adding/editing it.
    // This is a presentation-only filter: the backend re-validates the exact same permissions
    // independently at save time (WorkflowEndpoints.cs) regardless of what this dialog ever showed, so
    // removing a node from view here is not a security control — it's purely so a user never sees, or
    // can search up, a type their role can't use.
    const visibleSources = SOURCES.filter(s =>
      this.phaseCfg.isSourceEnabled(s.id) &&
      (!s.permissionPrefix || this.permissions.hasPermission(`${s.permissionPrefix}.view`))
    );
    byRank.set(0, visibleSources.map(s => ({
      id:       s.id,
      rank:     0,
      name:     s.name,
      sub:      s.sub,
      abbr:     s.abbr,
      color:    s.color,
      category: null,
      isSource: true,
      status:   (m === 'source' ? 'enabled' : 'disabled') as ItemStatus,
    })));

    // Rank 1-9 — Transforms (hidden ranks and disabled items filtered by phase config)
    const pickerMap = new Map(pm?.items.map(i => [i.id, i]) ?? []);

    TRANSFORMS.forEach(t => {
      // Skip entire rank categories hidden by phase config
      if (this.phaseCfg.isRankHidden(t.rank)) return;
      // Hide items not enabled in this phase (don't show as disabled)
      if (!this.phaseCfg.isTransformEnabled(t.id)) return;
      // RBAC: a destination type with a dedicated permission group (see transforms.data.ts) the current
      // role lacks View for is excluded from the library entirely, same as a phase-hidden item — see
      // the matching comment on the Sources filter above for why this is presentation-only, not a
      // security control, and for the View/Create/Edit distinction.
      if (t.permissionPrefix && !this.permissions.hasPermission(`${t.permissionPrefix}.view`)) return;

      const meta = TRANSFORM_META[t.id] ?? { abbr: t.name.slice(0, 3).toUpperCase(), color: '#64748B' };
      let status: ItemStatus = 'disabled';
      let reason: string | null = null;

      if (m === 'transform') {
        // Phase config: if not enabled, keep disabled regardless of pipeline state
        if (!this.phaseCfg.isTransformEnabled(t.id)) {
          status = 'disabled';
          reason = 'Not available in this phase';
        } else {
          const pi = pickerMap.get(t.id);
          status = pi ? (pi.status as ItemStatus) : 'disabled';
          reason = pi?.reason ?? null;
        }

        // Lock the other destination type while mid-way through configuring one —
        // switching would silently discard the in-progress form.
        if ((t.id === 'dest-sqlserver' || t.id === 'dest-csv' || t.id === 'dest-mysql' || t.id === 'dest-mongo' || t.id === 'dest-postgres' || t.id === 'dest-fhir' || t.id === 'dest-blob' || t.id === 'dest-medplum' || t.id === 'dest-azurefhir') && this.destTypeLocked()) {
          status = 'disabled';
          reason = 'Finish or go back to Configure before switching destination type.';
        }
      }

      const item: LibraryItem = {
        id: t.id, rank: t.rank, name: t.name, sub: t.sub,
        abbr: meta.abbr, color: meta.color,
        category: t.category ?? null,
        isSource: false, status, reason,
        group: t.group ?? null,
      };

      if (!byRank.has(t.rank)) byRank.set(t.rank, []);
      byRank.get(t.rank)!.push(item);
    });

    // Merge option (transform mode only)
    if (m === 'transform' && pm?.mergeOpt) {
      const opt = pm.mergeOpt;
      const groupRank = TRANSFORMS.find(t => t.group === opt.group)?.rank ?? 3;
      const mergeItem: LibraryItem = {
        id:       `__merge__${opt.group}`,
        rank:     groupRank,
        name:     opt.source ? 'Merge Sources' : `Merge · ${this.appSvc.groupLabel(opt.group)}`,
        sub:      `Fan ${opt.count} ${opt.source ? 'source connectors' : 'group members'} into one merge node.`,
        abbr:     '⊕',
        color:    '#10B981',
        category: null,
        isSource: false,
        status:   'show',
        isMerge:  true,
        mergeOpt: opt,
        group:    opt.group,
      };
      if (!byRank.has(groupRank)) byRank.set(groupRank, []);
      byRank.get(groupRank)!.push(mergeItem);
    }

    return [...byRank.entries()]
      .sort(([a], [b]) => a - b)
      .filter(([, items]) => items.length > 0)
      .map(([rank, items]) => ({
        rank,
        label:    RANK_LABEL[rank] ?? `Rank ${rank}`,
        icon:     RANK_META[rank]?.icon ?? '◉',
        catColor: RANK_META[rank]?.catColor ?? '#64748B',
        items,
      }));
  });

  // ── search-filtered categories ────────────────────────────────────────────
  readonly filteredCategories = computed<LibraryCategory[]>(() => {
    const q = this.searchQuery().trim().toLowerCase();
    if (!q) return this.allCategories();
    return this.allCategories()
      .map(cat => ({ ...cat, items: cat.items.filter(i =>
        i.name.toLowerCase().includes(q) || i.sub.toLowerCase().includes(q)
      )}))
      .filter(cat => cat.items.length > 0);
  });

  // ── selected item (resolved from id) ─────────────────────────────────────
  readonly selectedItem = computed<LibraryItem | undefined>(() => {
    const id = this.selectedId();
    if (!id) return undefined;
    for (const cat of this.allCategories()) {
      const found = cat.items.find(i => i.id === id);
      if (found) return found;
    }
    return undefined;
  });

  readonly rankLabel = RANK_LABEL;

  // ── template helpers ──────────────────────────────────────────────────────
  getRankColor(rank: number): string {
    return RANK_META[rank]?.catColor ?? '#64748B';
  }

  selectItem(item: LibraryItem): void {
    if (item.status === 'disabled' || item.status === 'hide') return;

    // Any registered source type (Epic, the other EHR vendors, Generic FHIR, HL7v2, Sample, ...) jumps straight
    // into its own config form — a single registry lookup instead of one `if (item.id === '...')` per source.
    if (item.isSource && SOURCE_FORM_REGISTRY[item.id]) {
      // Re-clicking the vendor that's already open would otherwise call openSourceForm() again — for the
      // WizardService-backed vendors (see SELF_CONTAINED_SOURCE_FORM_KEYS) that re-runs wiz.open(), which
      // repopulates every field signal from the underlying node/defaults and wipes whatever the user has
      // typed but not yet saved. Already showing this exact vendor's form — no-op.
      if (this.openSourceFormType() === item.id) return;
      this.openSourceForm(item.id);
      return;
    }
    if (item.id === 'dest-sqlserver' || item.id === 'dest-csv' || item.id === 'dest-mysql' || item.id === 'dest-mongo' || item.id === 'dest-postgres' || item.id === 'dest-fhir' || item.id === 'dest-blob' || item.id === 'dest-medplum' || item.id === 'dest-azurefhir') {
      const type: 'sql' | 'csv' | 'mysql' | 'mongo' | 'postgres' | 'medplum' | 'fhir' | 'blob' | 'azurefhir' =
        item.id === 'dest-sqlserver' ? 'sql' : item.id === 'dest-mysql' ? 'mysql' : item.id === 'dest-postgres' ? 'postgres' : item.id === 'dest-mongo' ? 'mongo' : item.id === 'dest-medplum' ? 'medplum' : item.id === 'dest-fhir' ? 'fhir' : item.id === 'dest-blob' ? 'blob' : item.id === 'dest-azurefhir' ? 'azurefhir' : 'csv';
      if (this.showDestWizard()) {
        // Already showing this exact destination type — _openDestWizard() unconditionally resets step,
        // attach node and mapping state, which would wipe the form for no reason. No-op instead.
        if (this.destWizardType() === type) return;
        // Switching to a *different* type after the user has already filled in later steps — or just typed
        // into Step 1 without ever clicking Next/Save (destWizardHasProgressed alone misses that; see
        // DestinationWizardComponent.isStep1Dirty()) — would silently discard that progress. Confirm first.
        if (this.destWizardHasProgressed() || this.destWizardRef()?.isStep1Dirty()) {
          this.pendingDestSwitch.set(type);
          return;
        }
      }
      this._openDestWizard(type);
      return;
    }

    this.selectedId.set(item.id);
  }

  destTypeLabel(type: 'sql' | 'csv' | 'mysql' | 'mongo' | 'postgres' | 'medplum' | 'fhir' | 'blob' | 'azurefhir' | null): string {
    return type === 'sql' ? 'SQL Server' : type === 'mysql' ? 'MySQL' : type === 'postgres' ? 'PostgreSQL' : type === 'mongo' ? 'MongoDB' : type === 'medplum' ? 'Medplum' : type === 'fhir' ? 'FHIR Repository (Aidbox)' : type === 'azurefhir' ? 'Azure FHIR Service' : type === 'blob' ? 'Azure Blob Storage' : 'CSV';
  }

  confirmDestSwitch(): void {
    const type = this.pendingDestSwitch();
    if (type) this._openDestWizard(type);
    this.pendingDestSwitch.set(null);
  }

  cancelDestSwitch(): void {
    this.pendingDestSwitch.set(null);
  }

  onConfirmSwitchBackdropClick(e: MouseEvent): void {
    if (e.target === e.currentTarget) this.cancelDestSwitch();
  }

  // ── inline source-config form (registry-driven — Epic, other EHR vendors, Generic FHIR, HL7v2, Sample) ────────
  /** Best-effort vendor/source-type detection for an existing canvas node, used when re-opening its form for
   *  editing (see the constructor's effect above) — now shared with canvas.component.ts's node-delete
   *  permission gate via source-node-vendor.util.ts, so the two never resolve a node's vendor differently. */
  private _sourceFormKeyForNode(node: CanvasNode): string {
    return sourceFormKeyForNode(node);
  }

  /** Opens the registered form for `key` (a sources.data.ts id) — self-contained vendor forms restore through
   *  WizardService.open()/editingFields() exactly as EpicAudienceFormComponent always did; headless forms
   *  (generic-fhir, hl7v2) restore through their own `initialFields` input, seeded from `editNodeOrId`'s fields. */
  // The single choke point every source vendor's form opens through, self-contained (Epic, Cerner,
  // Athenahealth, Allscripts, Healow, Meditech, Sample — each mutates PipelineStore directly, never
  // emitting sourceSelected/transformSelected) or headless (Generic FHIR, HL7 v2, Sample — routed
  // back through onHeadlessSourceFormSave() below). Gating here, rather than in each of those forms
  // individually, is what makes View/Create/Edit enforcement apply uniformly across all nine vendors:
  // an editNodeOrId present means this reopens an EXISTING node's config (Edit); absent means it's
  // about to add a brand-new one (Create).
  openSourceForm(key: string, editNodeOrId?: CanvasNode | string | null): void {
    const prefix = SOURCES.find(s => s.id === key)?.permissionPrefix;
    if (prefix) {
      const action = editNodeOrId ? 'edit' : 'create';
      if (!this.permissions.hasPermission(`${prefix}.${action}`)) {
        this.toast.error('Not permitted', `You don't have permission to ${action} this source.`);
        return;
      }
    }

    this.sourceFormError.set(null);
    if (SELF_CONTAINED_SOURCE_FORM_KEYS.has(key)) {
      const nodeId = typeof editNodeOrId === 'string' ? editNodeOrId : editNodeOrId?.id;
      this.wiz.open(nodeId ?? undefined);
      this.wiz.openedInline.set(true);
      this.sourceFormEditNode.set(null);
    } else {
      const node = typeof editNodeOrId === 'string' ? (this.store.byId(editNodeOrId) ?? null) : (editNodeOrId ?? null);
      this.sourceFormEditNode.set(node);
    }
    this.openSourceFormType.set(key);
  }

  onSourceFormSaved(): void {
    this._close();
  }

  onSourceFormCancelled(): void {
    this.wiz.close();
    this.openSourceFormType.set(null);
    this.sourceFormEditNode.set(null);
    this.sourceFormError.set(null);
  }

  /** Save button for a headless form (generic-fhir, hl7v2 — anything not in SELF_CONTAINED_SOURCE_FORM_KEYS) —
   *  generalizes the old onGenericFhirFormSave(), reading sources.data.ts for the new node's abbr/color/label
   *  instead of hardcoding Generic FHIR's. */
  onHeadlessSourceFormSave(): void {
    const instance = this.sourceFormOutlet()?.componentInstance as SourceConfigFormComponent | null | undefined;
    const fields = instance?.getFields();
    if (!fields) {
      this.sourceFormError.set('Fix the highlighted fields before saving.');
      return;
    }

    const editNode = this.sourceFormEditNode();
    if (editNode) {
      this.store.updateNode(editNode.id, { fields } as Partial<CanvasNode>);
    } else {
      const meta = SOURCES.find(s => s.id === this.openSourceFormType());
      const node: SourceNode = {
        id: this.store.nextNodeId(),
        kind: undefined,
        x: 360,
        y: 300,
        connected: true,
        abbr: meta?.abbr ?? 'SRC',
        color: meta?.color ?? '#5b6573',
        connectorLabel: meta?.name,
        fields,
      };
      this.store.addNode(node);
    }

    this._close();
  }

  // ── destination wizard ────────────────────────────────────────────────────
  // The wizard's own 'sql' type covers both SQL Server and Azure SQL (the specific DestinationType
  // is only chosen inside the wizard itself, not at this outer selection) — checked as an OR of
  // both permission prefixes here as a best-effort UI filter; the backend's own per-spec check in
  // WorkflowEndpoints.cs (which sees the actual chosen DestinationType once the form is submitted)
  // is what actually enforces the precise one.
  //
  // 'medplum' and 'fhir' have no DEDICATED backend permission group (no PermissionGroupCode.Medplum /
  // .FhirRepository member exists) — but that doesn't mean they're unenforced: SourceSystemPermissionGroups
  // .GroupFor(DestinationType.Medplum / .FhirRepository) falls back to the generic SourceConnections group
  // (no same-named PermissionGroupCode member => fallback, per that method's own doc comment), and
  // ControllerAuthorizationExtensions.HasPermissionAsync uses that exact resolution when a real
  // create/edit/delete request for either of these DestinationType values comes in. So the backend already
  // requires sourceconnections.create/.edit/.delete for these two — checking it here (instead of an empty
  // prefix list) is closing a UI/backend mismatch, not inventing a new code.
  private destWizardPermissionPrefixes(type: 'sql' | 'csv' | 'mysql' | 'mongo' | 'postgres' | 'medplum' | 'fhir' | 'blob' | 'azurefhir'): string[] {
    switch (type) {
      case 'sql':      return ['sqlserver', 'azuresql'];
      case 'csv':      return ['csv'];
      case 'mysql':    return ['mysql'];
      case 'mongo':    return ['mongo'];
      case 'postgres': return ['postgresql'];
      case 'blob':     return ['blobstorage'];
      case 'medplum':  return ['sourceconnections'];
      case 'fhir':     return ['sourceconnections'];
      // Azure FHIR Service likewise has no dedicated PermissionGroupCode member — same fallback-to-
      // SourceConnections resolution as 'medplum'/'fhir' above (see this method's doc comment).
      case 'azurefhir': return ['sourceconnections'];
    }
  }

  private canOpenDestWizard(type: 'sql' | 'csv' | 'mysql' | 'mongo' | 'postgres' | 'medplum' | 'fhir' | 'blob' | 'azurefhir', action: 'create' | 'edit'): boolean {
    const prefixes = this.destWizardPermissionPrefixes(type);
    if (!prefixes.length) return true;
    if (prefixes.some(prefix => this.permissions.hasPermission(`${prefix}.${action}`))) return true;
    this.toast.error('Not permitted', `You don't have permission to ${action} this destination.`);
    return false;
  }

  private _openDestWizard(type: 'sql' | 'csv' | 'mysql' | 'mongo' | 'postgres' | 'medplum' | 'fhir' | 'blob' | 'azurefhir'): void {
    if (!this.canOpenDestWizard(type, 'create')) return;
    const pm = this.pickerModel();
    if (!pm) return;
    this.destWizardType.set(type);
    this.destWizardAttach.set(pm.attachTo);
    this.destEditNode.set(null);
    this.destWizardStep.set(1);
    this.destWizardHasProgressed.set(false);
    this.destMappingCanvasActive.set(false);
    this.destMappingTitle.set(null);
    this.destMappingCount.set(0);
    this.destSuggestionCount.set(0);
    this.destZoomPercent.set('100%');
    this.mappingSearchQuery.set('');
    this.showDestWizard.set(true);
  }

  private _openDestWizardEdit(node: CanvasNode): void {
    const tId = (node as TransformNode).transformId;
    const type: 'sql' | 'csv' | 'mysql' | 'mongo' | 'postgres' | 'medplum' | 'fhir' | 'blob' | 'azurefhir' =
      tId === 'dest-sqlserver' ? 'sql' : tId === 'dest-mysql' ? 'mysql' : tId === 'dest-postgres' ? 'postgres' : tId === 'dest-mongo' ? 'mongo' : tId === 'dest-medplum' ? 'medplum' : tId === 'dest-fhir' ? 'fhir' : tId === 'dest-blob' ? 'blob' : tId === 'dest-azurefhir' ? 'azurefhir' : 'csv';
    if (!this.canOpenDestWizard(type, 'edit')) return;
    const inbound = this.store.inboundEdges(node.id);
    const parentId = inbound[0]?.from ?? '';
    const parentNode = parentId ? this.store.byId(parentId) : null;
    this.destWizardType.set(type);
    this.destWizardAttach.set(parentNode ?? node);
    this.destEditNode.set(node);
    this.destWizardStep.set(1);
    this.destWizardHasProgressed.set(false);
    this.showDestWizard.set(true);
  }

  onDestWizardSaved(e: AddTransformEvent): void {
    const editNode = this.destEditNode();
    this.transformSelected.emit(editNode ? { ...e, editNodeId: editNode.id } : e);
    this._close();
  }

  onDestWizardCancelled(): void {
    // The dest wizard's own "← Back to library" button emits this unconditionally (it has no notion of
    // "has this progressed") — unlike switching destination type or closing the whole dialog, this used to
    // discard silently, including the common case of typing into Step 1 and backing out without ever
    // clicking Next/Save (destWizardHasProgressed alone misses that — see isStep1Dirty()). Same "don't lose
    // unsaved work" guard as pendingDestSwitch/pendingCloseConfirm.
    if (this.destWizardHasProgressed() || this.destWizardRef()?.isStep1Dirty()) {
      this.pendingDestCancel.set(true);
      return;
    }
    this._resetDestWizard();
  }

  confirmDestCancel(): void {
    this.pendingDestCancel.set(false);
    this._resetDestWizard();
  }

  cancelDestCancel(): void {
    this.pendingDestCancel.set(false);
  }

  onConfirmCancelBackdropClick(e: MouseEvent): void {
    if (e.target === e.currentTarget) this.cancelDestCancel();
  }

  private _resetDestWizard(): void {
    this.showDestWizard.set(false);
    this.destWizardType.set(null);
    this.destWizardAttach.set(null);
    this.destEditNode.set(null);
    this.destWizardStep.set(1);
    this.destWizardHasProgressed.set(false);
    this.destMappingCanvasActive.set(false);
    this.destMappingTitle.set(null);
    this.destMappingCount.set(0);
    this.destSuggestionCount.set(0);
    this.destZoomPercent.set('100%');
    this.mappingSearchQuery.set('');
  }

  // ── add to pipeline (fallback for items without an auto-open form) ───────
  addSelected(): void {
    const item = this.selectedItem();
    if (!item) return;

    if (item.isSource) {
      // Every SOURCES entry is registered today (see selectItem()) — this is a defensive fallback for a source
      // id that somehow isn't, matching the pre-refactor "epic jumps to its form, everything else falls through
      // to sourceSelected" split.
      if (SOURCE_FORM_REGISTRY[item.id]) {
        this.openSourceForm(item.id);
        return;
      }
      this._close();
      this.sourceSelected.emit(item.id);
      return;
    }

    if (item.isMerge && item.mergeOpt) {
      this._close();
      this.mergeSelected.emit({ opt: item.mergeOpt });
      return;
    }

    const pm = this.pickerModel();
    if (!pm) return;
    this._close();
    this.transformSelected.emit({ attachNode: pm.attachTo, transformId: item.id, status: item.status });
  }

  close(): void { this._close(); }

  // Bound to app-modal-overlay's (closed) output — fires on Escape unconditionally, and on backdrop
  // click whenever closeOnBackdropClick is true. Escape isn't covered by that input (see
  // ModalOverlayComponent.onEscape), so a form/wizard being open is checked here instead: closing outright
  // would otherwise silently discard whatever the user has entered.
  onOverlayClosed(): void {
    if (this.openSourceFormType() !== null || this.showDestWizard()) {
      this.pendingCloseConfirm.set(true);
      return;
    }
    this.close();
  }

  confirmDiscardClose(): void {
    this.pendingCloseConfirm.set(false);
    this.close();
  }

  cancelDiscardClose(): void {
    this.pendingCloseConfirm.set(false);
  }

  onConfirmCloseBackdropClick(e: MouseEvent): void {
    if (e.target === e.currentTarget) this.cancelDiscardClose();
  }

  private _close(): void {
    this.closed.emit();
    this.selectedId.set(null);
    this.searchQuery.set('');
    this.sidebarPinned.set(false);
    this.openSourceFormType.set(null);
    this.sourceFormEditNode.set(null);
    this.sourceFormError.set(null);
    this.showDestWizard.set(false);
    this.destWizardType.set(null);
    this.destWizardAttach.set(null);
    this.destEditNode.set(null);
    this.destWizardStep.set(1);
    this.destWizardHasProgressed.set(false);
    this.destMappingCanvasActive.set(false);
    this.destMappingTitle.set(null);
    this.destMappingCount.set(0);
    this.destSuggestionCount.set(0);
    this.destZoomPercent.set('100%');
    this.mappingSearchQuery.set('');
    this.pendingDestSwitch.set(null);
    this.wiz.close();
  }
}
