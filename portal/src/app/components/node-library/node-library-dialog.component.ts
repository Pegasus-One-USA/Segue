import { Component, input, output, inject, computed, signal, effect, untracked, viewChild } from '@angular/core';
import { ModalOverlayComponent } from '../shared/modal-overlay/modal-overlay.component';
import { PipelineStore } from '../../services/pipeline.store';
import { ApplicabilityService } from '../../services/applicability.service';
import { PhaseConfigService } from '../../services/phase-config.service';
import { WizardService } from '../../services/wizard.service';
import { WorkflowGraphMapperService } from '../../services/workflow-graph-mapper.service';
import { SOURCES } from '../../data/sources.data';
import { TRANSFORMS } from '../../data/transforms.data';
import { RANK_LABEL } from '../../models/transform.model';
import { CanvasNode, SourceNode, TransformNode, isSourceNode } from '../../models/node.model';
import { MergeNodeOption } from '../../models/wizard-state.model';
import { EpicAudienceFormComponent } from '../epic-source-wizard/epic-audience-form/epic-audience-form.component';
import { DestinationWizardComponent } from './destination-wizard/destination-wizard.component';
import { GenericFhirSourceFormComponent } from './generic-fhir-source-form/generic-fhir-source-form.component';
import { ZoomDockComponent } from '../canvas/zoom-dock/zoom-dock.component';

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
    EpicAudienceFormComponent,
    DestinationWizardComponent,
    GenericFhirSourceFormComponent,
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

  // ── inline Epic form state ────────────────────────────────────────────────
  readonly showEpicForm = signal(false);

  // ── inline Generic FHIR form state ────────────────────────────────────────
  readonly showGenericFhirForm = signal(false);
  readonly genericFhirEditNode = signal<CanvasNode | null>(null);
  readonly genericFhirForm = viewChild(GenericFhirSourceFormComponent);
  readonly genericFhirError = signal<string | null>(null);

  // ── sidebar collapsed state (auto when a form opens, user-toggleable) ─────
  readonly sidebarPinned = signal(false);
  readonly isSidebarMini = computed(() =>
    (this.showEpicForm() || this.showDestWizard() || this.showGenericFhirForm()) && !this.sidebarPinned()
  );

  toggleSidebar(): void { this.sidebarPinned.update(v => !v); }

  // ── destination wizard state ──────────────────────────────────────────────
  readonly showDestWizard   = signal(false);
  readonly destWizardType   = signal<'sql' | 'csv' | 'mysql' | 'mongo' | 'postgres' | 'fhir' | null>(null);
  readonly destWizardAttach = signal<CanvasNode | null>(null);
  readonly destEditNode     = signal<CanvasNode | null>(null);

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

  // Whether the whole dialog is expanded to near-fullscreen (see .nld--maximized) — the maximize button
  // lives in several places (mapping-canvas header, floating controls, and each form's own topbar) but
  // they all toggle this one piece of state.
  readonly isMaximized = signal(false);
  toggleMaximize(): void { this.isMaximized.update(v => !v); }

  // Mirrors the open canvas's own destination type / mapping count so the header can show them
  // without reaching into the wizard's nested-@if template (a template ref there is out of scope here).
  readonly destMappingTypeLabel = computed(() => this.destWizardType() === 'sql' ? 'SQL Server' : 'CSV');
  readonly destMappingCount = signal(0);

  readonly pendingDestSwitch      = signal<'sql' | 'csv' | 'mysql' | 'mongo' | 'postgres' | 'fhir' | null>(null);

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
          if (tId === 'dest-sqlserver' || tId === 'dest-csv' || tId === 'dest-mysql' || tId === 'dest-mongo' || tId === 'dest-postgres' || tId === 'dest-fhir') {
            untracked(() => this._openDestWizardEdit(node));
          }
          return;
        }
        if (node && this._isGenericFhirNode(node)) {
          untracked(() => this.openGenericFhirForm(node));
          return;
        }
        untracked(() => this.openEpicForm(id));
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

    // Rank 0 — Sources (filtered by phase config: only enabled sources shown)
    const visibleSources = SOURCES.filter(s => this.phaseCfg.isSourceEnabled(s.id));
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
        if ((t.id === 'dest-sqlserver' || t.id === 'dest-csv' || t.id === 'dest-mysql' || t.id === 'dest-mongo' || t.id === 'dest-postgres' || t.id === 'dest-fhir') && this.destTypeLocked()) {
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

    // Epic and the destination connectors jump straight into their config form.
    if (item.id === 'epic') {
      this.openEpicForm();
      return;
    }
    if (item.id === 'generic-fhir') {
      this.openGenericFhirForm(null);
      return;
    }
    if (item.id === 'dest-sqlserver' || item.id === 'dest-csv' || item.id === 'dest-mysql' || item.id === 'dest-mongo' || item.id === 'dest-postgres' || item.id === 'dest-fhir') {
      const type: 'sql' | 'csv' | 'mysql' | 'mongo' | 'postgres' | 'fhir' =
        item.id === 'dest-sqlserver' ? 'sql' : item.id === 'dest-mysql' ? 'mysql' : item.id === 'dest-postgres' ? 'postgres' : item.id === 'dest-mongo' ? 'mongo' : item.id === 'dest-fhir' ? 'fhir' : 'csv';
      // Switching type after the user has already filled in later steps would
      // silently discard that progress — confirm first.
      if (this.showDestWizard() && this.destWizardType() !== type && this.destWizardHasProgressed()) {
        this.pendingDestSwitch.set(type);
        return;
      }
      this._openDestWizard(type);
      return;
    }

    this.selectedId.set(item.id);
  }

  destTypeLabel(type: 'sql' | 'csv' | 'mysql' | 'mongo' | 'postgres' | 'fhir' | null): string {
    return type === 'sql' ? 'SQL Server' : type === 'mysql' ? 'MySQL' : type === 'postgres' ? 'PostgreSQL' : type === 'mongo' ? 'MongoDB' : type === 'fhir' ? 'FHIR Repository (Aidbox)' : 'CSV';
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

  // ── inline Epic form ──────────────────────────────────────────────────────
  openEpicForm(nodeId?: string | null): void {
    this.wiz.open(nodeId ?? undefined);
    this.wiz.openedInline.set(true);
    this.showEpicForm.set(true);
  }

  onEpicFormSaved(): void {
    this._close();
  }

  onEpicFormCancelled(): void {
    this.wiz.close();
    this.showEpicForm.set(false);
  }

  // ── inline Generic FHIR form ──────────────────────────────────────────────
  private _isGenericFhirNode(node: CanvasNode): boolean {
    return isSourceNode(node) && /generic.?fhir/i.test(node.fields['Connector'] ?? node.connectorLabel ?? '');
  }

  openGenericFhirForm(editNode: CanvasNode | null): void {
    this.genericFhirEditNode.set(editNode);
    this.genericFhirError.set(null);
    this.showGenericFhirForm.set(true);
  }

  onGenericFhirFormCancelled(): void {
    this.showGenericFhirForm.set(false);
    this.genericFhirEditNode.set(null);
    this.genericFhirError.set(null);
  }

  onGenericFhirFormSave(): void {
    const fields = this.genericFhirForm()?.getFields();
    if (!fields) {
      this.genericFhirError.set('Fix the highlighted fields before saving.');
      return;
    }

    const editNode = this.genericFhirEditNode();
    if (editNode) {
      this.store.updateNode(editNode.id, { fields } as Partial<CanvasNode>);
    } else {
      const node: SourceNode = {
        id: this.store.nextNodeId(),
        kind: undefined,
        x: 360,
        y: 300,
        connected: true,
        abbr: 'R4',
        color: '#5b6573',
        connectorLabel: 'Generic FHIR R4',
        fields,
      };
      this.store.addNode(node);
    }

    this._close();
  }

  // ── destination wizard ────────────────────────────────────────────────────
  private _openDestWizard(type: 'sql' | 'csv' | 'mysql' | 'mongo' | 'postgres' | 'fhir'): void {
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
    this.showDestWizard.set(true);
  }

  private _openDestWizardEdit(node: CanvasNode): void {
    const tId = (node as TransformNode).transformId;
    const type: 'sql' | 'csv' | 'mysql' | 'mongo' | 'postgres' | 'fhir' =
      tId === 'dest-sqlserver' ? 'sql' : tId === 'dest-mysql' ? 'mysql' : tId === 'dest-postgres' ? 'postgres' : tId === 'dest-mongo' ? 'mongo' : tId === 'dest-fhir' ? 'fhir' : 'csv';
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
  }

  // ── add to pipeline (fallback for items without an auto-open form) ───────
  addSelected(): void {
    const item = this.selectedItem();
    if (!item) return;

    if (item.isSource) {
      if (item.id === 'epic') {
        this.openEpicForm();
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
    if (this.showEpicForm() || this.showDestWizard() || this.showGenericFhirForm()) {
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
    this.showEpicForm.set(false);
    this.showGenericFhirForm.set(false);
    this.genericFhirEditNode.set(null);
    this.genericFhirError.set(null);
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
    this.pendingDestSwitch.set(null);
    this.wiz.close();
  }
}
